using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Library.Api.Middleware;

/// <summary>
/// Gives the API's error shape to the failures that never reach
/// <see cref="GlobalExceptionHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> <c>GlobalExceptionHandler</c> only sees thrown
/// exceptions. A request that dies during model binding - malformed JSON, a
/// string where an int belongs, an unknown property, a bad enum in the query
/// string - never reaches an action, so nothing throws and MVC writes its own
/// default 400. The same was true of 415 and 405, produced by the framework
/// before any of our code runs.
/// </para>
/// <para>
/// That left the API with two error formats: one carrying <c>errorCode</c> and
/// one not. Since the documented contract is that clients branch on the code and
/// humans read the message, a whole family of responses a client must handle was
/// unbranchable - which is worse than merely inconsistent, because it stays
/// invisible until somebody writes the client.
/// </para>
/// <para>
/// <b>The second job: not leaking internals.</b> System.Text.Json's binding
/// messages name .NET types - <c>"could not be mapped to any .NET member
/// contained in type 'Library.Application.Members.Requests.CreateMemberRequest'"</c>.
/// That hands a caller our assembly layout, namespace structure and DTO names for
/// free. <c>GlobalExceptionHandler</c> already refuses to leak exception text for
/// exactly this reason; these messages were bypassing that rule. They are
/// rewritten into something a client can act on that says nothing about how the
/// server is built.
/// </para>
/// </remarks>
internal static partial class RequestProblemDetails
{
    /// <summary>Builds the 400 for a request that failed model binding.</summary>
    public static IActionResult ForInvalidModelState(ActionContext context)
    {
        (string errorCode, string detail) = Classify(context.ModelState);

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The request could not be read",
            Detail = detail,
            Type = $"https://httpstatuses.io/{StatusCodes.Status400BadRequest}",
            Instance =
                $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}",
        };

        problemDetails.Extensions["errorCode"] = errorCode;
        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        problemDetails.Extensions["errors"] = BuildErrors(context.ModelState);

        return new ObjectResult(problemDetails)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Fills in the shape for framework-generated responses - 415 on a wrong
    /// content type, 405 on a wrong verb, 404 on an unmatched route.
    /// </summary>
    /// <remarks>
    /// Runs for <i>every</i> ProblemDetails the pipeline writes, including the
    /// ones <see cref="GlobalExceptionHandler"/> has already filled in, so each
    /// field is written only when it is missing. Overwriting would replace a
    /// specific domain code such as <c>member.not_found</c> with a generic one.
    /// </remarks>
    public static void Customize(ProblemDetailsContext context)
    {
        int status = context.ProblemDetails.Status
                     ?? context.HttpContext.Response.StatusCode;

        context.ProblemDetails.Type ??= $"https://httpstatuses.io/{status}";
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

        if (!context.ProblemDetails.Extensions.ContainsKey("errorCode"))
        {
            context.ProblemDetails.Extensions["errorCode"] = CodeForStatus(status);
        }

        if (!context.ProblemDetails.Extensions.ContainsKey("traceId"))
        {
            context.ProblemDetails.Extensions["traceId"] =
                Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        }
    }

    private static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "request.invalid",
        StatusCodes.Status401Unauthorized => "auth.unauthenticated",
        StatusCodes.Status403Forbidden => "auth.forbidden",
        StatusCodes.Status404NotFound => "route.not_found",
        StatusCodes.Status405MethodNotAllowed => "request.method_not_allowed",
        StatusCodes.Status406NotAcceptable => "request.not_acceptable",
        StatusCodes.Status415UnsupportedMediaType => "request.unsupported_media_type",
        StatusCodes.Status429TooManyRequests => "request.rate_limited",
        >= 500 => "server.unexpected_error",
        _ => "request.failed",
    };

    /// <summary>
    /// Picks the most specific code for what actually went wrong, so a client can
    /// tell "you sent a field I do not accept" from "that is not JSON at all".
    /// </summary>
    private static (string ErrorCode, string Detail) Classify(ModelStateDictionary modelState)
    {
        bool unknownProperty = false;
        bool typeMismatch = false;
        bool bodyMissing = false;
        bool jsonBroken = false;

        foreach (ModelStateEntry entry in modelState.Values)
        {
            foreach (ModelError error in entry.Errors)
            {
                string message = error.ErrorMessage;

                if (message.Contains(
                    "could not be mapped to any .NET member", StringComparison.Ordinal))
                {
                    unknownProperty = true;
                }
                else if (message.Contains(
                    "non-empty request body is required", StringComparison.Ordinal))
                {
                    bodyMissing = true;
                }
                else if (message.Contains("could not be converted", StringComparison.Ordinal))
                {
                    typeMismatch = true;
                }
                else if (message.Contains("Path: $", StringComparison.Ordinal))
                {
                    jsonBroken = true;
                }
            }
        }

        if (bodyMissing)
        {
            return ("request.body_required", "A request body is required.");
        }

        if (unknownProperty)
        {
            return (
                "request.unknown_property",
                "The request contains a property this endpoint does not accept. "
                + "See the errors property for which one.");
        }

        if (typeMismatch)
        {
            return (
                "request.type_mismatch",
                "A value in the request is the wrong type for its field. "
                + "See the errors property for details.");
        }

        if (jsonBroken)
        {
            return ("request.malformed_json", "The request body is not valid JSON.");
        }

        return (
            "request.invalid",
            "One or more values in the request are not valid. "
            + "See the errors property for details.");
    }

    /// <summary>Per-field messages, with the framework's internals filtered out.</summary>
    /// <remarks>
    /// When the body fails to parse, MVC reports both the JSON-path error and a
    /// bare <c>"The request field is required."</c> against the action's parameter
    /// name - the parameter is null only <i>because</i> the parse failed.
    /// Reporting both sends the caller hunting for a second problem that does not
    /// exist, so the derived one is dropped whenever a real JSON-path error is
    /// present.
    /// </remarks>
    private static Dictionary<string, string[]> BuildErrors(ModelStateDictionary modelState)
    {
        // A body-level key is either a JSON path ("$", "$.membershipTypeId") or
        // the empty string, which is what MVC uses for "A non-empty request body
        // is required." Both mean the body itself was the problem.
        bool hasBodyErrors = modelState.Keys.Any(IsBodyKey);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach ((string key, ModelStateEntry entry) in modelState)
        {
            if (entry.Errors.Count == 0)
            {
                continue;
            }

            if (hasBodyErrors && !IsBodyKey(key))
            {
                continue;
            }

            errors[FieldName(key)] = entry.Errors
                .Select(error => Sanitize(error.ErrorMessage))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return errors;
    }

    /// <summary>True when this ModelState key describes the body, not a field.</summary>
    private static bool IsBodyKey(string key) =>
        key.Length == 0 || key.StartsWith('$');

    /// <summary>Turns a JSON path into the field name the client actually sent.</summary>
    private static string FieldName(string key) => key switch
    {
        "$" or "" => "body",
        _ when key.StartsWith("$.", StringComparison.Ordinal) => key[2..],
        _ => key,
    };

    /// <summary>
    /// Rewrites a binding message into one that is useful to the caller and
    /// silent about the server.
    /// </summary>
    private static string Sanitize(string message)
    {
        Match unmapped = UnmappedMemberPattern().Match(message);

        if (unmapped.Success)
        {
            return $"Unknown property '{unmapped.Groups[1].Value}' is not accepted here.";
        }

        if (message.Contains("could not be converted", StringComparison.Ordinal))
        {
            return "The value supplied is not valid for this field.";
        }

        if (message.Contains("non-empty request body is required", StringComparison.Ordinal))
        {
            return "A request body is required.";
        }

        if (message.Contains("Path: $", StringComparison.Ordinal))
        {
            return "The request body is not valid JSON.";
        }

        // Binding messages for query and route values name the value and the
        // field and nothing else - "The value 'NotAStatus' is not valid for
        // Status." Those are already what we would have written, so they pass
        // through. Anything still naming a type is a message we have not seen
        // before; it gets replaced rather than trusted.
        return TypeNamePattern().IsMatch(message)
            ? "The value supplied is not valid for this field."
            : message;
    }

    [GeneratedRegex(
        @"The JSON property '([^']+)' could not be mapped",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnmappedMemberPattern();

    [GeneratedRegex(
        @"\b(System|Microsoft|Library)\.[A-Za-z0-9_.]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePattern();
}
