using Library.Domain.Common;

namespace Library.Domain.Events;

/// <summary>
/// Raised when a copy is returned. Carries everything a handler needs to assess a
/// fine without loading the loan again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the event carries values rather than the entity.</b> A handler runs
/// after the transaction has committed, on a different <c>DbContext</c>. Handing
/// it a <c>Loan</c> instance would hand it an entity tracked by a context that is
/// already gone — and tempt it to mutate state that nothing will save. Passing
/// the facts keeps the handler honest about what it is allowed to do.
/// </para>
/// <para>
/// <b>Why <c>DaysOverdue</c> is computed here and not in the handler.</b> The
/// overdue span is a fact about the moment of return, established by the entity
/// using the clock the caller supplied. If the handler recomputed it against
/// "now", a dispatch delayed by a slow queue would quietly charge the member for
/// the delay.
/// </para>
/// </remarks>
/// <param name="LoanId">The loan that was closed.</param>
/// <param name="MemberId">Who returned it — the member the fine attaches to.</param>
/// <param name="BookCopyId">Which physical copy came back.</param>
/// <param name="DueAt">When it was due.</param>
/// <param name="ReturnedAt">When it actually came back.</param>
/// <param name="DaysOverdue">Whole days late; zero when returned on time.</param>
public sealed record LoanReturnedEvent(
    int LoanId,
    int MemberId,
    int BookCopyId,
    DateTimeOffset DueAt,
    DateTimeOffset ReturnedAt,
    int DaysOverdue) : DomainEvent
{
    /// <summary>True when this return should result in a fine.</summary>
    public bool IsLate => DaysOverdue > 0;
}
