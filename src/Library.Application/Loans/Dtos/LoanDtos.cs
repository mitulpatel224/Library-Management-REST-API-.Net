using Library.Domain.Enums;

namespace Library.Application.Loans.Dtos;

/// <summary>One row in the loan listing.</summary>
/// <remarks>
/// Carries the barcode, title and member name rather than only their ids: the
/// listing is read at a desk, and "MEM-2026-00042 has BC-0007" is not something a
/// human can act on without two more lookups.
/// </remarks>
public sealed record LoanSummaryDto
{
    public int Id { get; init; }

    public int BookCopyId { get; init; }

    public string Barcode { get; init; } = string.Empty;

    public string BookTitle { get; init; } = string.Empty;

    public int MemberId { get; init; }

    public string MembershipNumber { get; init; } = string.Empty;

    public string MemberName { get; init; } = string.Empty;

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public DateTimeOffset? ReturnedAt { get; init; }

    /// <summary>
    /// Computed against the clock at read time, never stored.
    /// </summary>
    /// <remarks>
    /// A loan becomes overdue at midnight with nothing writing to its row, so a
    /// persisted column would be wrong between the due date and whenever a job
    /// next ran. See <see cref="LoanStatus"/>.
    /// </remarks>
    public LoanStatus Status { get; init; }

    /// <summary>Whole days late. Zero unless overdue.</summary>
    public int DaysOverdue { get; init; }

    /// <summary>Null until a fine has been assessed for this loan.</summary>
    public decimal? FineAmount { get; init; }

    public bool FineSettled { get; init; }
}

/// <summary>A single loan, with everything the desk needs to discuss it.</summary>
public sealed record LoanDetailDto
{
    public int Id { get; init; }

    public int BookCopyId { get; init; }

    public string Barcode { get; init; } = string.Empty;

    public int BookId { get; init; }

    public string BookTitle { get; init; } = string.Empty;

    public string? Isbn { get; init; }

    public int MemberId { get; init; }

    public string MembershipNumber { get; init; } = string.Empty;

    public string MemberName { get; init; } = string.Empty;

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public DateTimeOffset? ReturnedAt { get; init; }

    public LoanStatus Status { get; init; }

    public int DaysOverdue { get; init; }

    /// <summary>How many days remain before it is due. Negative once overdue.</summary>
    public int DaysRemaining { get; init; }

    public FineDto? Fine { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>A fine, and what has happened to it.</summary>
public sealed record FineDto
{
    public int Id { get; init; }

    public int LoanId { get; init; }

    public int MemberId { get; init; }

    public string MembershipNumber { get; init; } = string.Empty;

    public string MemberName { get; init; } = string.Empty;

    public int DaysOverdue { get; init; }

    /// <summary>The rate applied when this fine was assessed, not the current rate.</summary>
    public decimal RatePerDay { get; init; }

    public decimal Amount { get; init; }

    /// <summary>Zero once paid or waived. This is the figure that gates borrowing.</summary>
    public decimal OutstandingAmount { get; init; }

    public DateTimeOffset? PaidAt { get; init; }

    public DateTimeOffset? WaivedAt { get; init; }

    public string? WaivedReason { get; init; }

    public bool IsSettled { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>What a member currently owes, and whether it stops them borrowing.</summary>
public sealed record MemberBalanceDto
{
    public int MemberId { get; init; }

    public string MembershipNumber { get; init; } = string.Empty;

    public decimal TotalOutstanding { get; init; }

    public int UnsettledFineCount { get; init; }

    public int ActiveLoanCount { get; init; }

    public int OverdueLoanCount { get; init; }

    public int MaxConcurrentLoans { get; init; }

    /// <summary>False when the member is at their limit.</summary>
    public bool CanBorrowMore { get; init; }
}
