using System.Diagnostics;
using FluentValidation;
using Library.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Middleware;

/// <summary>
/// Translates every unhandled exception into an RFC 9457 <c>ProblemDetails</c> response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The alternative is a try/catch in every controller
/// action. That duplicates the same eight lines dozens of times, and the
/// duplication is not the worst part - the worst part is that the one action
/// where somebody forgets returns a stack trace to the caller. Centralising the
/// translation makes the safe behaviour the default and the only behaviour.
/// </para>
/// <para>
/// <b>Why <c>IExceptionHandler</c> and not custom middleware.</b> Before .NET 8
/// this was written as a middleware component wrapping <c>next(context)</c> in a
/// try/catch. <c>IExceptionHandler</c> (registered via
/// <c>AddExceptionHandler</c> + <c>UseExceptionHandler</c>) is the first-party
/// replacement: handlers are tried in registration order, each returning
/// <c>true</c> if it handled the exception, and the framework owns the plumbing.
/// </para>
/// <para>
/// <b>The security rule this enforces.</b> A <see cref="DomainException"/> is a
/// message written for the caller, so its text is returned. Anything else is a
/// bug, so the caller gets a generic message and a trace id, while the real
/// exception goes to the log. Leaking a stack trace hands an attacker your
/// framework versions, file paths, and often your SQL schema - it is OWASP's
/// "Security Misconfiguration" in its most common form.
/// </para>
/// <para>
/// Every response carries <c>traceId</c> from <see cref="Activity.Current"/>.
/// That is what makes a user's screenshot of an error actionable: the same id is
/// on every log line for that request.
/// </para>
/// </remarks>
public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        IHostEnvironment environment,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _environment = environment;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (int status, string title, string detail, string errorCode) = Map(exception);

        // Materialised into locals BEFORE the logging call. Request.Path is a
        // PathString, and passing it directly makes the compiler insert an
        // implicit string conversion INSIDE the log call - work that happens even
        // when the level is disabled. Analyzer CA1873 flags exactly that.
        string method = httpContext.Request.Method;
        string path = httpContext.Request.Path.Value ?? string.Empty;

        // Expected 4xx outcomes are information, not incidents - logging them as
        // errors trains everyone to ignore the error log. Genuine faults are
        // logged with the full exception.
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(method, path, exception);
        }
        else
        {
            LogRequestFailed(status, errorCode, method, path, detail);
        }

        httpContext.Response.StatusCode = status;

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = $"{httpContext.Request.Method} {httpContext.Request.Path}",
        };

        problemDetails.Extensions["errorCode"] = errorCode;
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Per-field validation failures, so a client can highlight the offending inputs.
        if (exception is ValidationException validationException)
        {
            problemDetails.Extensions["errors"] = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(e => e.ErrorMessage).ToArray());
        }

        // Outside Production only: attach the exception type and stack trace to
        // save a trip to the logs while developing.
        if (!_environment.IsProduction() && status >= StatusCodes.Status500InternalServerError)
        {
            problemDetails.Extensions["exception"] = exception.GetType().FullName;
            problemDetails.Extensions["stackTrace"] = exception.StackTrace;
        }

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }

    /// <summary>
    /// The single place where an exception type becomes an HTTP status code.
    /// </summary>
    /// <remarks>
    /// Adding a new domain exception means adding one arm here. If that arm is
    /// forgotten the result is a 500, which is loud and gets noticed - the right
    /// failure mode for a mistake of this kind.
    /// </remarks>
    private static (int Status, string Title, string Detail, string ErrorCode) Map(
        Exception exception) => exception switch
    {
        NotFoundException ex => (
            StatusCodes.Status404NotFound,
            "Resource not found",
            ex.Message,
            ex.ErrorCode),

        ConflictException ex => (
            StatusCodes.Status409Conflict,
            "Request conflicts with the current state of the resource",
            ex.Message,
            ex.ErrorCode),

        BusinessRuleViolationException ex => (
            StatusCodes.Status422UnprocessableEntity,
            "Business rule violated",
            ex.Message,
            ex.ErrorCode),

        // Catch-all for any future DomainException subtype. Placed after the
        // specific arms, because C# pattern matching takes the first match.
        DomainException ex => (
            StatusCodes.Status400BadRequest,
            "Invalid request",
            ex.Message,
            ex.ErrorCode),

        ValidationException => (
            StatusCodes.Status422UnprocessableEntity,
            "One or more validation errors occurred",
            "The request did not pass validation. See the errors property for details.",
            "validation.failed"),

        // The client hung up or a timeout fired. 499 is nginx's non-standard
        // code for it; there is no RFC status for "caller went away".
        OperationCanceledException => (
            499,
            "Request cancelled",
            "The request was cancelled before it completed.",
            "request.cancelled"),

        UnauthorizedAccessException => (
            StatusCodes.Status403Forbidden,
            "Forbidden",
            "You do not have permission to perform this action.",
            "auth.forbidden"),

        // Anything else is a bug. Deliberately opaque to the caller.
        _ => (
            StatusCodes.Status500InternalServerError,
            "An unexpected error occurred",
            "An unexpected error occurred while processing your request.",
            "server.unexpected_error"),
    };

    // -----------------------------------------------------------------------
    // Source-generated logging.
    //
    // Analyzer CA1848 flags the conventional _logger.LogError("...", a, b) call,
    // and the reason is worth understanding rather than suppressing: that form
    // boxes every value-type argument and allocates a params object[] on EVERY
    // invocation - including the ones where the level is disabled and the
    // message is discarded immediately.
    //
    // [LoggerMessage] moves that work to compile time. The generator emits a
    // cached, strongly-typed delegate that checks IsEnabled BEFORE touching the
    // arguments. On an error path this is the difference between allocating on
    // every failed request and allocating on none.
    //
    // These partial methods have no body - the source generator writes it, using
    // the _logger field it finds on this class. That is why the class is partial.
    // -----------------------------------------------------------------------

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unhandled exception on {Method} {Path}")]
    private partial void LogUnhandledException(string method, string path, Exception exception);

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Request failed with {StatusCode} ({ErrorCode}) on {Method} {Path}: {Detail}")]
    private partial void LogRequestFailed(
        int statusCode, string errorCode, string method, string path, string detail);
}
