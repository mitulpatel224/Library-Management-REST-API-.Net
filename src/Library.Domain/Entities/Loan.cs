using Library.Domain.Common;
using Library.Domain.Enums;
using Library.Domain.Events;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// One lending transaction: a member takes a physical copy home and brings it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invariant this type exists to protect:</b> a copy that is out cannot be
/// issued again. That rule is enforced in three places, and only one of them is a
/// guarantee:
/// </para>
/// <list type="number">
///   <item><c>BookCopy.MarkOnLoan()</c> refuses a copy that is not Available —
///     this produces the readable error.</item>
///   <item><c>LoanService</c> checks for an existing active loan — this produces
///     the readable error earlier, before any write.</item>
///   <item>The filtered unique index <c>Loan(BookCopyId) WHERE ReturnedAt IS
///     NULL</c> — <b>this is the guarantee</b>. The two checks above both read
///     before they write, so two concurrent requests can pass them both.</item>
/// </list>
/// <para>
/// <b>Why <c>ReturnedAt</c> is nullable rather than a <c>bool IsReturned</c>.</b>
/// Null means "still out", which is exactly what the filtered index keys on — one
/// nullable column carries the state and the timestamp together, and makes the
/// partial index expressible. A boolean would need a second column to answer
/// "when", and the two could disagree.
/// </para>
/// <para>
/// <b>No clock inside.</b> Every method that needs the time takes it as a
/// parameter. <c>Library.Domain</c> references nothing, so it cannot hold
/// <c>IClock</c>, and reading <c>DateTimeOffset.UtcNow</c> here would make overdue
/// and fine calculations untestable without changing the machine's date.
/// </para>
/// </remarks>
public sealed class Loan : AuditableEntity
{
    private Loan() { }

    private Loan(
        int bookCopyId,
        int memberId,
        DateTimeOffset issuedAt,
        DateTimeOffset dueAt)
    {
        BookCopyId = bookCopyId;
        MemberId = memberId;
        IssuedAt = issuedAt;
        DueAt = dueAt;
    }

    public int BookCopyId { get; private set; }

    public BookCopy BookCopy { get; private set; } = null!;

    public int MemberId { get; private set; }

    public Member Member { get; private set; } = null!;

    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>
    /// When the copy is due back. Derived at issue from the member's
    /// <c>MembershipType.LoanPeriodDays</c>.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed. The loan period is a property of the
    /// membership type, which can be edited — and a member who was told
    /// "due in 14 days" must not silently acquire a different due date because a
    /// librarian later changed the Standard type to 7 days. The promise made at
    /// the desk is the one the fine is measured against.
    /// </remarks>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>Null while the copy is still out. This is what the filtered index keys on.</summary>
    public DateTimeOffset? ReturnedAt { get; private set; }

    /// <summary>The fine assessed for returning late, if any.</summary>
    public Fine? Fine { get; private set; }

    /// <summary>True while the copy is still out.</summary>
    public bool IsReturned => ReturnedAt.HasValue;

    /// <summary>
    /// Whole days past the due date, measured at <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts <b>whole</b> days, so a copy one hour late is not yet a day overdue
    /// and attracts no fine. Charging ₹50 for being an hour late is not a rule a
    /// librarian wants to defend at the desk.
    /// </para>
    /// <para>
    /// Once returned, the span is measured to the return, not to now — otherwise a
    /// fine for a book returned last year would keep growing.
    /// </para>
    /// </remarks>
    public int DaysOverdueAt(DateTimeOffset asOf)
    {
        DateTimeOffset measuredAt = ReturnedAt ?? asOf;

        if (measuredAt <= DueAt)
        {
            return 0;
        }

        return (int)(measuredAt - DueAt).TotalDays;
    }

    /// <summary>Whether this loan is active, overdue, or closed at a given moment.</summary>
    public LoanStatus StatusAt(DateTimeOffset asOf)
    {
        if (IsReturned)
        {
            return LoanStatus.Returned;
        }

        return asOf > DueAt ? LoanStatus.Overdue : LoanStatus.Active;
    }

    /// <summary>
    /// Issues a copy to a member, marking the copy as on loan.
    /// </summary>
    /// <param name="copy">The physical copy. Marked <c>OnLoan</c> as a side effect.</param>
    /// <param name="member">The borrower. Must be permitted to borrow.</param>
    /// <param name="issuedAt">Now, from the caller's <c>IClock</c>.</param>
    /// <param name="loanPeriodDays">From the member's membership type.</param>
    /// <exception cref="ConflictException">The copy is not available.</exception>
    /// <exception cref="BusinessRuleViolationException">
    /// The member may not borrow, or the loan period is not positive.
    /// </exception>
    public static Loan Issue(
        BookCopy copy,
        Member member,
        DateTimeOffset issuedAt,
        int loanPeriodDays)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(member);

        // Asked of the member rather than tested as Status == Active. Phase 4 is
        // where CanBorrow is expected to grow a condition about unpaid fines, and
        // every caller should inherit that without being edited.
        if (!member.CanBorrow)
        {
            throw new BusinessRuleViolationException(
                "member.cannot_borrow",
                $"Member '{member.MembershipNumber}' may not borrow (status: {member.Status}).");
        }

        if (loanPeriodDays <= 0)
        {
            throw new BusinessRuleViolationException(
                "loan.invalid_period",
                "Loan period must be at least one day.");
        }

        // Throws if the copy is not Available, so the loan is never constructed
        // for a copy that cannot leave the building.
        copy.MarkOnLoan();

        return new Loan(copy.Id, member.Id, issuedAt, issuedAt.AddDays(loanPeriodDays));
    }

    /// <summary>
    /// Closes the loan and returns the copy to the shelf.
    /// </summary>
    /// <remarks>
    /// Raises <see cref="LoanReturnedEvent"/> for the post-commit dispatcher to
    /// act on. The fine is deliberately <b>not</b> created here: assessing it
    /// inside this method would write a fine inside the same transaction as the
    /// return, and the whole reason events dispatch after the commit is that a
    /// rollback must not leave a member fined for a return that never happened.
    /// </remarks>
    /// <param name="returnedAt">Now, from the caller's <c>IClock</c>.</param>
    /// <param name="condition">Optional updated condition of the copy.</param>
    /// <exception cref="ConflictException">The loan is already closed.</exception>
    public void Return(DateTimeOffset returnedAt, CopyCondition? condition = null)
    {
        if (IsReturned)
        {
            throw new ConflictException(
                "loan.already_returned",
                $"This loan was already closed on {ReturnedAt:yyyy-MM-dd}.");
        }

        if (returnedAt < IssuedAt)
        {
            throw new BusinessRuleViolationException(
                "loan.return_before_issue",
                "A copy cannot be returned before it was issued.");
        }

        ReturnedAt = returnedAt;
        BookCopy?.MarkReturned(condition);

        RaiseDomainEvent(new LoanReturnedEvent(
            Id,
            MemberId,
            BookCopyId,
            DueAt,
            returnedAt,
            DaysOverdueAt(returnedAt)));
    }

    /// <summary>
    /// Extends the due date by a further period.
    /// </summary>
    /// <remarks>
    /// Refused once overdue: renewing a late loan would erase the fine that has
    /// already accrued, which turns "return it late and renew" into a way of never
    /// paying. A member in that position has to return the copy and settle up.
    /// </remarks>
    /// <exception cref="ConflictException">The loan is closed or already overdue.</exception>
    public void Renew(DateTimeOffset asOf, int additionalDays)
    {
        if (IsReturned)
        {
            throw new ConflictException(
                "loan.already_returned",
                "A closed loan cannot be renewed.");
        }

        if (asOf > DueAt)
        {
            throw new ConflictException(
                "loan.overdue_cannot_renew",
                "An overdue loan cannot be renewed. Return the copy and settle the fine.");
        }

        if (additionalDays <= 0)
        {
            throw new BusinessRuleViolationException(
                "loan.invalid_period",
                "Renewal period must be at least one day.");
        }

        DueAt = DueAt.AddDays(additionalDays);
    }

    /// <summary>Attaches the fine assessed for this loan. Called by the fine handler.</summary>
    internal void AttachFine(Fine fine) => Fine = fine;
}
