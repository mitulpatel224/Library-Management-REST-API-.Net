using FluentValidation;
using Library.Application.Loans.Requests;

namespace Library.Application.Loans.Validators;

/// <summary>
/// Validation for <see cref="IssueLoanRequest"/>.
/// </summary>
/// <remarks>
/// Only shape is checked here — that the two ids were supplied at all. Whether the
/// copy exists, whether it is already out, and whether the member is at their
/// limit are all questions about <i>current state</i>, which a validator cannot
/// answer without a database round trip that would then be stale by the time the
/// insert ran. Those live in <c>LoanService</c>, and the ones that must hold under
/// concurrency live in the database.
/// </remarks>
public sealed class IssueLoanRequestValidator : AbstractValidator<IssueLoanRequest>
{
    public IssueLoanRequestValidator()
    {
        RuleFor(r => r.BookCopyId)
            .GreaterThan(0).WithMessage("A book copy is required.");

        RuleFor(r => r.MemberId)
            .GreaterThan(0).WithMessage("A member is required.");
    }
}
