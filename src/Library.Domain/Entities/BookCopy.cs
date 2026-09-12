using Library.Domain.Common;
using Library.Domain.Enums;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// A single physical item on a shelf - the thing a member actually carries home.
/// </summary>
/// <remarks>
/// <para>
/// This is the loanable unit. Loans reference a <see cref="BookCopy"/>, never a
/// <see cref="Book"/>, which is what lets a library lend one of its three copies
/// of a title while the other two stay available.
/// </para>
/// <para>
/// <see cref="Barcode"/> is the human-facing key: it is what gets scanned at the
/// desk, and it is unique across the entire library, not merely within a title.
/// </para>
/// </remarks>
public sealed class BookCopy : AuditableEntity
{
    private BookCopy() { }

    private BookCopy(
        int bookId,
        string barcode,
        CopyCondition condition,
        string? shelfLocation,
        DateOnly? acquiredOn)
    {
        BookId = bookId;
        Barcode = barcode;
        Condition = condition;
        ShelfLocation = shelfLocation;
        AcquiredOn = acquiredOn;
        Status = CopyStatus.Available;
    }

    public int BookId { get; private set; }

    public Book Book { get; private set; } = null!;

    /// <summary>Scannable identifier, unique library-wide.</summary>
    public string Barcode { get; private set; } = null!;

    public CopyStatus Status { get; private set; }

    public CopyCondition Condition { get; private set; }

    /// <summary>Where to find it, e.g. "A-12-3".</summary>
    public string? ShelfLocation { get; private set; }

    public DateOnly? AcquiredOn { get; private set; }

    /// <summary>True when this copy can be issued right now.</summary>
    public bool IsAvailable => Status == CopyStatus.Available;

    public static BookCopy Create(
        int bookId,
        string barcode,
        CopyCondition condition = CopyCondition.New,
        string? shelfLocation = null,
        DateOnly? acquiredOn = null)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            throw new BusinessRuleViolationException(
                "copy.barcode_required", "Copy barcode is required.");
        }

        return new BookCopy(
            bookId,
            barcode.Trim().ToUpperInvariant(),
            condition,
            shelfLocation?.Trim(),
            acquiredOn);
    }

    /// <summary>
    /// Marks the copy as issued. Called by the loan workflow in Phase 4.
    /// </summary>
    /// <remarks>
    /// Guarding on <see cref="IsAvailable"/> here gives a clear error message,
    /// but it is NOT what makes the rule safe under concurrency - two requests
    /// can both read Available before either writes. The filtered unique index on
    /// <c>Loan(BookCopyId) WHERE ReturnedAt IS NULL</c> is what actually settles
    /// the race. This check is the friendly message; the index is the guarantee.
    /// </remarks>
    public void MarkOnLoan()
    {
        if (Status != CopyStatus.Available)
        {
            throw new ConflictException(
                "copy.not_available",
                $"Copy '{Barcode}' is not available for loan (current status: {Status}).");
        }

        Status = CopyStatus.OnLoan;
    }

    /// <summary>Returns the copy to the shelf, optionally recording new condition.</summary>
    public void MarkReturned(CopyCondition? condition = null)
    {
        if (condition is not null)
        {
            Condition = condition.Value;
        }

        // A copy returned in pieces does not go back into circulation.
        Status = Condition == CopyCondition.Poor ? CopyStatus.Damaged : CopyStatus.Available;
    }

    public void MarkLost() => Status = CopyStatus.Lost;

    public void Withdraw()
    {
        if (Status == CopyStatus.OnLoan)
        {
            throw new ConflictException(
                "copy.on_loan",
                $"Copy '{Barcode}' cannot be withdrawn while it is on loan.");
        }

        Status = CopyStatus.Withdrawn;
    }

    /// <summary>Returns a withdrawn or damaged copy to circulation.</summary>
    public void Reinstate()
    {
        if (Status == CopyStatus.OnLoan)
        {
            throw new ConflictException(
                "copy.on_loan", $"Copy '{Barcode}' is already on loan.");
        }

        Status = CopyStatus.Available;
    }

    public void UpdateDetails(CopyCondition condition, string? shelfLocation)
    {
        Condition = condition;
        ShelfLocation = shelfLocation?.Trim();
    }
}
