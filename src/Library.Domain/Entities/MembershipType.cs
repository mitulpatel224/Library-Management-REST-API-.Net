using Library.Domain.Common;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// A class of membership — Standard, Student, Staff — carrying the borrowing
/// limits that apply to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a lookup table with teeth.</b> The two numeric columns are not
/// description: Phase 4 reads <see cref="MaxConcurrentLoans"/> to decide whether
/// a member may borrow at all, and <see cref="LoanPeriodDays"/> to compute
/// <c>Loan.DueAt</c> — which is what every overdue calculation and fine derives
/// from.
/// </para>
/// <para>
/// <b>Why a table rather than an enum with a switch.</b> Both values are policy,
/// and policy changes. A librarian extending the student loan period from 14
/// days to 21 should be a row update, not a deployment. Modelling it as an enum
/// would put a business decision in a place only a developer can reach — the
/// same reasoning that puts the fine rate in configuration rather than a
/// constant.
/// </para>
/// </remarks>
public sealed class MembershipType : AuditableEntity
{
    /// <summary>Upper bound on any single membership type, as a sanity guard.</summary>
    public const int MaxAllowedConcurrentLoans = 50;

    /// <summary>Roughly a year; beyond this it is a typo, not a policy.</summary>
    public const int MaxAllowedLoanPeriodDays = 365;

    private MembershipType() { }

    private MembershipType(string name, int maxConcurrentLoans, int loanPeriodDays, string? description)
    {
        Name = name;
        MaxConcurrentLoans = maxConcurrentLoans;
        LoanPeriodDays = loanPeriodDays;
        Description = description;
    }

    public string Name { get; private set; } = null!;

    /// <summary>How many copies a member of this type may hold at once.</summary>
    public int MaxConcurrentLoans { get; private set; }

    /// <summary>Days from issue to due date. Phase 4 computes <c>DueAt</c> from this.</summary>
    public int LoanPeriodDays { get; private set; }

    public string? Description { get; private set; }

    private readonly List<Member> _members = [];
    public IReadOnlyCollection<Member> Members => _members.AsReadOnly();

    public static MembershipType Create(
        string name,
        int maxConcurrentLoans,
        int loanPeriodDays,
        string? description = null)
    {
        Validate(name, maxConcurrentLoans, loanPeriodDays);

        return new MembershipType(
            name.Trim(), maxConcurrentLoans, loanPeriodDays, description?.Trim());
    }

    public void Update(string name, int maxConcurrentLoans, int loanPeriodDays, string? description)
    {
        Validate(name, maxConcurrentLoans, loanPeriodDays);

        Name = name.Trim();
        MaxConcurrentLoans = maxConcurrentLoans;
        LoanPeriodDays = loanPeriodDays;
        Description = description?.Trim();
    }

    /// <remarks>
    /// Shared by <see cref="Create"/> and <see cref="Update"/> so the two cannot
    /// drift — an update that skipped a rule the factory enforces would let an
    /// entity reach a state it could never have been created in.
    /// </remarks>
    private static void Validate(string name, int maxConcurrentLoans, int loanPeriodDays)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "membership_type.name_required", "Membership type name is required.");
        }

        // Zero would create a membership that can never borrow — almost
        // certainly a mistake, and indistinguishable from one if allowed.
        if (maxConcurrentLoans is < 1 or > MaxAllowedConcurrentLoans)
        {
            throw new BusinessRuleViolationException(
                "membership_type.invalid_loan_limit",
                $"Maximum concurrent loans must be between 1 and {MaxAllowedConcurrentLoans}.");
        }

        if (loanPeriodDays is < 1 or > MaxAllowedLoanPeriodDays)
        {
            throw new BusinessRuleViolationException(
                "membership_type.invalid_loan_period",
                $"Loan period must be between 1 and {MaxAllowedLoanPeriodDays} days.");
        }
    }
}
