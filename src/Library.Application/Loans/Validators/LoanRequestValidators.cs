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

/// <summary>
/// Validation for <see cref="RenewLoanRequest"/>.
/// </summary>
/// <remarks>
/// The upper bound exists because <c>additionalDays</c> is caller-supplied and
/// unbounded renewal is indistinguishable from never returning the book. A year
/// is generous enough for any legitimate extension and finite enough that the due
/// date still means something.
/// </remarks>
public sealed class RenewLoanRequestValidator : AbstractValidator<RenewLoanRequest>
{
    public const int MaxRenewalDays = 365;

    public RenewLoanRequestValidator()
    {
        RuleFor(r => r.AdditionalDays)
            .InclusiveBetween(1, MaxRenewalDays)
            .When(r => r.AdditionalDays.HasValue)
            .WithMessage($"Renewal must be between 1 and {MaxRenewalDays} days.");
    }
}

public sealed class WaiveFineRequestValidator : AbstractValidator<WaiveFineRequest>
{
    public WaiveFineRequestValidator()
    {
        RuleFor(r => r.Reason)
            .NotEmpty().WithMessage("A reason is required when waiving a fine.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

public sealed class PayFineRequestValidator : AbstractValidator<PayFineRequest>
{
    public PayFineRequestValidator()
    {
        RuleFor(r => r.Note)
            .MaximumLength(500).WithMessage("Note must be 500 characters or fewer.");
    }
}

/// <remarks>
/// Only the date ranges are checked, for the same reason as the member listing:
/// page, page size and sort field are clamped because there is a sensible value to
/// clamp to, while an inverted range has none and would silently return an empty
/// page that reads like a real answer.
/// </remarks>
public sealed class LoanSearchRequestValidator : AbstractValidator<LoanSearchRequest>
{
    public LoanSearchRequestValidator()
    {
        RuleFor(r => r.IssuedTo)
            .GreaterThanOrEqualTo(r => r.IssuedFrom!.Value)
            .When(r => r.IssuedFrom.HasValue && r.IssuedTo.HasValue)
            .WithMessage("'issuedTo' cannot be earlier than 'issuedFrom'.");

        RuleFor(r => r.DueTo)
            .GreaterThanOrEqualTo(r => r.DueFrom!.Value)
            .When(r => r.DueFrom.HasValue && r.DueTo.HasValue)
            .WithMessage("'dueTo' cannot be earlier than 'dueFrom'.");
    }
}

public sealed class FineSearchRequestValidator : AbstractValidator<FineSearchRequest>
{
    public FineSearchRequestValidator()
    {
        RuleFor(r => r.AssessedTo)
            .GreaterThanOrEqualTo(r => r.AssessedFrom!.Value)
            .When(r => r.AssessedFrom.HasValue && r.AssessedTo.HasValue)
            .WithMessage("'assessedTo' cannot be earlier than 'assessedFrom'.");
    }
}
