namespace Library.Domain.Enums;

/// <summary>
/// The state of a <see cref="Entities.Loan"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not a stored column.</b> It is computed from
/// <c>ReturnedAt</c> and <c>DueAt</c> against the current time, because two of
/// the three values change without anything writing to the row: a loan becomes
/// <see cref="Overdue"/> at midnight on its due date whether or not anyone
/// touches it.
/// </para>
/// <para>
/// Persisting it would mean a nightly job to keep it honest, and a row that is
/// silently wrong between the due date and the next run. Every query that cares
/// derives it from the dates instead, which is always correct and never stale.
/// </para>
/// <para>
/// It is still an enum rather than a pair of booleans so that the API can filter
/// on one legible value, and so the three states stay mutually exclusive by
/// construction.
/// </para>
/// </remarks>
public enum LoanStatus
{
    /// <summary>Out, and not yet past its due date.</summary>
    Active = 0,

    /// <summary>Out, and past its due date. Accruing a fine.</summary>
    Overdue = 1,

    /// <summary>Returned. Whether it was returned late is a separate question.</summary>
    Returned = 2,
}
