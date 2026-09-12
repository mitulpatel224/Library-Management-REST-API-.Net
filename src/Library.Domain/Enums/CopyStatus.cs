namespace Library.Domain.Enums;

/// <summary>
/// Lifecycle state of a physical <see cref="Entities.BookCopy"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>On <see cref="OnLoan"/> and the single source of truth.</b> This status is
/// a denormalised cache of a fact that the <c>Loan</c> table already owns: a copy
/// is on loan exactly when a loan row exists for it with no <c>ReturnedAt</c>.
/// Storing it twice means it can disagree with itself.
/// </para>
/// <para>
/// It is kept anyway, for one reason: catalogue search filters on availability
/// constantly, and a column read beats a correlated subquery on every listing
/// query. The rule that makes it safe is that <b>only</b> the loan workflow
/// writes it, in the same transaction as the loan row, and the filtered unique
/// index on <c>Loan(BookCopyId) WHERE ReturnedAt IS NULL</c> remains the real
/// enforcement. The column is an optimisation; the index is the truth.
/// </para>
/// <para>
/// Values are explicitly numbered because they are persisted. Letting the
/// compiler assign them means inserting a new member in the middle silently
/// renumbers every value after it - and reinterprets existing rows.
/// </para>
/// </remarks>
public enum CopyStatus
{
    /// <summary>On the shelf and available to issue.</summary>
    Available = 0,

    /// <summary>Currently issued to a member.</summary>
    OnLoan = 1,

    /// <summary>Reported lost. Not loanable; retained for reporting.</summary>
    Lost = 2,

    /// <summary>Too damaged to circulate.</summary>
    Damaged = 3,

    /// <summary>Removed from circulation deliberately (weeded, archived).</summary>
    Withdrawn = 4,
}

/// <summary>Physical condition of a copy, recorded at acquisition and on return.</summary>
public enum CopyCondition
{
    New = 0,
    Good = 1,
    Fair = 2,
    Poor = 3,
}
