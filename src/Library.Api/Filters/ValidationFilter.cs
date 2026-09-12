using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Library.Api.Filters;

/// <summary>
/// Runs the FluentValidation validator for every action argument that has one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a filter is needed at all.</b> The old
/// <c>FluentValidation.AspNetCore</c> package hooked validators into MVC's
/// model-validation pipeline automatically. It is **deprecated** — the author
/// removed auto-validation because running validators inside model binding made
/// failures hard to trace and prevented validators from using async rules. The
/// supported replacement is to invoke validation explicitly, which is what this
/// filter does at one well-defined point.
/// </para>
/// <para>
/// <b>Why here and not in every controller action.</b> Three lines of
/// boilerplate per action is not the problem; the action where someone forgets
/// is. Centralising means an unvalidated request is impossible rather than
/// merely unlikely.
/// </para>
/// <para>
/// <b>Why it throws instead of returning a result.</b> Throwing
/// <see cref="ValidationException"/> routes the failure through
/// <c>GlobalExceptionHandler</c>, which already renders a 422 with a per-field
/// <c>errors</c> object. Returning a <c>BadRequestObjectResult</c> here would
/// create a second error shape that happens to look similar — and clients would
/// then have to parse both.
/// </para>
/// <para>
/// Validators are resolved per-request from DI, so a validator may itself depend
/// on scoped services.
/// </para>
/// </remarks>
public sealed class ValidationFilter : IAsyncActionFilter
{
    private readonly IServiceProvider _serviceProvider;

    public ValidationFilter(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        List<ValidationFailure>? failures = null;

        foreach (object? argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            // IValidator<T> for the argument's runtime type. Absent for types
            // that need no validation (an int id, a CancellationToken), in which
            // case there is nothing to do.
            Type validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());

            if (_serviceProvider.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            ValidationResult result = await validator.ValidateAsync(
                new ValidationContext<object>(argument),
                context.HttpContext.RequestAborted);

            if (!result.IsValid)
            {
                (failures ??= []).AddRange(result.Errors);
            }
        }

        if (failures is { Count: > 0 })
        {
            // Handled by GlobalExceptionHandler -> 422 + per-field errors.
            throw new ValidationException(failures);
        }

        await next();
    }
}
