using Library.Domain.Common;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// Money owed for returning a copy late.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the rate is not stored on this row but the amount is.</b> The amount is
/// a fact about a past event — what this member was actually charged — and must
/// not move when the library changes its rate next year. The rate itself lives in
/// configuration behind <c>FineRateResolver</c>; this row records the outcome of
/// applying it, along with the inputs, so the charge can be explained to the
/// member who is disputing it.
/// </para>
/// <para>
/// <b>Why a separate entity rather than a column on <c>Loan</c>.</b> A fine has
/// its own lifecycle: it is assessed, then later paid or waived, each with its own
/// timestamp and reason. Columns on <c>Loan</c> would mean a closed loan whose row
/// keeps changing long after the copy is back on the shelf.
/// </para>
/// </remarks>
public sealed class Fine : AuditableEntity
{
    /// <summary>Largest fine this system will assess without a librarian intervening.</summary>
    /// <remarks>
    /// A copy forgotten for three years would otherwise accrue an uncollectable
    /// five-figure charge that nobody will ever pay and everybody has to argue
    /// about. At the cap, the conversation moves from "pay the fine" to "replace
    /// the book", which is the right conversation.
    /// </remarks>
    public const decimal MaxAmount = 5000m;

    private Fine() { }

    private Fine(int loanId, int memberId, int daysOverdue, decimal ratePerDay, decimal amount)
    {
        LoanId = loanId;
        MemberId = memberId;
        DaysOverdue = daysOverdue;
        RatePerDay = ratePerDay;
        Amount = amount;
    }

    public int LoanId { get; private set; }

    public Loan Loan { get; private set; } = null!;

    /// <summary>
    /// Denormalised from the loan so "what does this member owe?" is one indexed
    /// query rather than a join through every loan they have ever had.
    /// </summary>
    public int MemberId { get; private set; }

    public Member Member { get; private set; } = null!;

    /// <summary>Days late at the moment of return. Recorded so the charge can be explained.</summary>
    public int DaysOverdue { get; private set; }

    /// <summary>The rate applied, captured at assessment. Not a live lookup.</summary>
    public decimal RatePerDay { get; private set; }

    public decimal Amount { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? WaivedAt { get; private set; }

    /// <summary>Why a librarian waived this fine. Required when waiving.</summary>
    public string? WaivedReason { get; private set; }

    public bool IsSettled => PaidAt.HasValue || WaivedAt.HasValue;

    /// <summary>Still owed — the figure that blocks borrowing.</summary>
    public decimal OutstandingAmount => IsSettled ? 0m : Amount;

    /// <summary>
    /// Assesses a fine for a late return.
    /// </summary>
    /// <param name="loanId">The closed loan.</param>
    /// <param name="memberId">The borrower.</param>
    /// <param name="daysOverdue">Whole days late. Must be positive.</param>
    /// <param name="ratePerDay">From <c>FineRateResolver</c>, not a constant.</param>
    /// <exception cref="BusinessRuleViolationException">
    /// The return was not late, or the rate is negative.
    /// </exception>
    public static Fine Assess(int loanId, int memberId, int daysOverdue, decimal ratePerDay)
    {
        if (daysOverdue <= 0)
        {
            throw new BusinessRuleViolationException(
                "fine.not_overdue",
                "A fine cannot be assessed for a copy returned on time.");
        }

        if (ratePerDay < 0m)
        {
            throw new BusinessRuleViolationException(
                "fine.invalid_rate",
                "Fine rate cannot be negative.");
        }

        decimal amount = Math.Min(daysOverdue * ratePerDay, MaxAmount);

        return new Fine(loanId, memberId, daysOverdue, ratePerDay, amount);
    }

    /// <summary>Records payment in full.</summary>
    /// <exception cref="ConflictException">Already paid or waived.</exception>
    public void Pay(DateTimeOffset paidAt)
    {
        if (PaidAt.HasValue)
        {
            throw new ConflictException(
                "fine.already_paid",
                $"This fine was already paid on {PaidAt:yyyy-MM-dd}.");
        }

        if (WaivedAt.HasValue)
        {
            throw new ConflictException(
                "fine.already_waived",
                "This fine was waived and is not payable.");
        }

        PaidAt = paidAt;
    }

    /// <summary>
    /// Cancels the fine, recording why.
    /// </summary>
    /// <remarks>
    /// The reason is required for the same reason a suspension's is: a charge
    /// dropped without explanation is one nobody can audit, and waiving fines is
    /// exactly the operation worth being able to review afterwards.
    /// </remarks>
    /// <exception cref="ConflictException">Already paid or waived.</exception>
    /// <exception cref="BusinessRuleViolationException">No reason supplied.</exception>
    public void Waive(DateTimeOffset waivedAt, string reason)
    {
        if (PaidAt.HasValue)
        {
            throw new ConflictException(
                "fine.already_paid",
                "A paid fine cannot be waived. Refund it instead.");
        }

        if (WaivedAt.HasValue)
        {
            throw new ConflictException(
                "fine.already_waived",
                $"This fine was already waived on {WaivedAt:yyyy-MM-dd}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleViolationException(
                "fine.waiver_reason_required",
                "A reason is required when waiving a fine.");
        }

        WaivedAt = waivedAt;
        WaivedReason = reason.Trim();
    }
}
