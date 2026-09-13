using FluentValidation;
using Library.Application.Common.Abstractions;
using Library.Application.Members.Requests;
using Library.Domain.Entities;
using Library.Domain.ValueObjects;

namespace Library.Application.Members.Validators;

/// <summary>
/// Validation for <see cref="CreateMemberRequest"/>.
/// </summary>
/// <remarks>
/// The email and phone rules delegate to the value objects rather than
/// re-implementing them. Two definitions of "valid email" would drift, and the
/// value object is the one the domain actually enforces — this class only turns
/// its verdict into a per-field message.
/// </remarks>
public sealed class CreateMemberRequestValidator : AbstractValidator<CreateMemberRequest>
{
    public CreateMemberRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(r => r.FullName)
            .NotEmpty().WithMessage("Member name is required.")
            .MaximumLength(Member.MaxNameLength)
            .WithMessage($"Name must be {Member.MaxNameLength} characters or fewer.");

        RuleFor(r => r.Email)
            .NotEmpty().WithMessage("Email address is required.")
            .Must(BeAValidEmail)
            .WithMessage("Email address is not valid.");

        RuleFor(r => r.Phone)
            .Must(BeAValidPhone)
            .When(r => !string.IsNullOrWhiteSpace(r.Phone))
            .WithMessage("Phone number is not valid.");

        RuleFor(r => r.MembershipTypeId)
            .GreaterThan(0).WithMessage("A membership type is required.");

        RuleFor(r => r.Address)
            .MaximumLength(500).WithMessage("Address must be 500 characters or fewer.");

        // The join year is baked into the membership number, so both ends matter:
        // a future date issues MEM-2031-00042 today, and a mis-keyed century
        // issues MEM-1825-00042 just as permanently.
        //
        // Upper bound is clock.Today, not Today.AddDays(1). The AddDays(1) that
        // stood here accepted tomorrow while the message said "cannot be in the
        // future" - the rule and its explanation disagreed, and the message was
        // the one telling the truth about the intent.
        RuleFor(r => r.JoinedOn)
            .LessThanOrEqualTo(_ => clock.Today)
            .When(r => r.JoinedOn.HasValue)
            .WithMessage("Join date cannot be in the future.");

        RuleFor(r => r.JoinedOn)
            .GreaterThanOrEqualTo(Member.EarliestJoinDate)
            .When(r => r.JoinedOn.HasValue)
            .WithMessage(
                $"Join date cannot be before {Member.EarliestJoinDate:yyyy-MM-dd}.");
    }

    internal static bool BeAValidEmail(string email) => Email.TryCreate(email, out _, out _);

    internal static bool BeAValidPhone(string? phone) =>
        PhoneNumber.TryCreate(phone, out _, out _);
}

public sealed class UpdateMemberRequestValidator : AbstractValidator<UpdateMemberRequest>
{
    public UpdateMemberRequestValidator()
    {
        RuleFor(r => r.FullName)
            .NotEmpty().WithMessage("Member name is required.")
            .MaximumLength(Member.MaxNameLength)
            .WithMessage($"Name must be {Member.MaxNameLength} characters or fewer.");

        RuleFor(r => r.Email)
            .NotEmpty().WithMessage("Email address is required.")
            .Must(CreateMemberRequestValidator.BeAValidEmail)
            .WithMessage("Email address is not valid.");

        RuleFor(r => r.Phone)
            .Must(CreateMemberRequestValidator.BeAValidPhone)
            .When(r => !string.IsNullOrWhiteSpace(r.Phone))
            .WithMessage("Phone number is not valid.");

        RuleFor(r => r.MembershipTypeId)
            .GreaterThan(0).WithMessage("A membership type is required.");

        RuleFor(r => r.Address)
            .MaximumLength(500).WithMessage("Address must be 500 characters or fewer.");
    }
}

/// <remarks>
/// The reason is required here <i>and</i> in <c>Member.Suspend</c>. That is not
/// redundancy: this produces the per-field message a form can display, and the
/// entity guarantees the rule holds for every caller — including the seeder and
/// any future import path that never passes through a validator.
/// </remarks>
public sealed class SuspendMemberRequestValidator : AbstractValidator<SuspendMemberRequest>
{
    public SuspendMemberRequestValidator()
    {
        RuleFor(r => r.Reason)
            .NotEmpty().WithMessage("A reason is required when suspending a member.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

public sealed class CancelMemberRequestValidator : AbstractValidator<CancelMemberRequest>
{
    public CancelMemberRequestValidator()
    {
        RuleFor(r => r.Reason)
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

public sealed class CreateMembershipTypeRequestValidator
    : AbstractValidator<CreateMembershipTypeRequest>
{
    public CreateMembershipTypeRequestValidator()
    {
        RuleFor(r => r.Name)
            .NotEmpty().WithMessage("Membership type name is required.")
            .MaximumLength(50).WithMessage("Name must be 50 characters or fewer.");

        RuleFor(r => r.Description)
            .MaximumLength(500).WithMessage("Description must be 500 characters or fewer.");

        // Bounds mirror MembershipType's own guards. The entity is the authority;
        // these exist so the caller gets a field-level message rather than a
        // domain exception rendered as a bare 422.
        RuleFor(r => r.MaxConcurrentLoans)
            .InclusiveBetween(1, MembershipType.MaxAllowedConcurrentLoans)
            .WithMessage(
                "Maximum concurrent loans must be between 1 and " +
                $"{MembershipType.MaxAllowedConcurrentLoans}.");

        RuleFor(r => r.LoanPeriodDays)
            .InclusiveBetween(1, MembershipType.MaxAllowedLoanPeriodDays)
            .WithMessage(
                $"Loan period must be between 1 and {MembershipType.MaxAllowedLoanPeriodDays} days.");
    }
}

/// <summary>
/// Validation for the member listing's query string.
/// </summary>
/// <remarks>
/// <para>
/// Only the date range is checked. Page, page size and sort field are all
/// <i>clamped</i> rather than rejected — <c>PageRequest.Normalize()</c> forces
/// the numbers into range and <c>MemberSortOptions.Resolve()</c> falls back to
/// the default for an unknown field. That asymmetry is deliberate: a caller
/// asking for too many rows still has a sensible answer, so refusing them helps
/// nobody.
/// </para>
/// <para>
/// An inverted range is different. <c>joinedFrom=2030-01-01&amp;joinedTo=2000-01-01</c>
/// cannot match anything, so the empty page it used to return was indistinguishable
/// from "no members joined in that window" — the caller reads a real answer to a
/// question they did not mean to ask. There is no sensible value to clamp to, so
/// this one is refused.
/// </para>
/// </remarks>
public sealed class MemberSearchRequestValidator : AbstractValidator<MemberSearchRequest>
{
    public MemberSearchRequestValidator()
    {
        RuleFor(r => r.JoinedTo)
            .GreaterThanOrEqualTo(r => r.JoinedFrom!.Value)
            .When(r => r.JoinedFrom.HasValue && r.JoinedTo.HasValue)
            .WithMessage("'joinedTo' cannot be earlier than 'joinedFrom'.");
    }
}
