using Library.Application.Common.Models;
using Library.Domain.Enums;

namespace Library.Application.Loans.Requests;

/// <summary>
/// Payload for issuing a copy to a member.
/// </summary>
/// <remarks>
/// <para>
/// Note what is absent: <c>issuedAt</c> and <c>dueAt</c>. Both are the server's to
/// decide — the issue time comes from <c>IClock</c>, and the due date from the
/// member's <c>MembershipType.LoanPeriodDays</c>. Accepting either would let a
/// caller grant themselves a longer loan than their membership allows, or
/// back-date an issue to avoid a fine.
/// </para>
/// <para>
/// The copy is identified by id rather than barcode because the barcode is a
/// scanned human-facing string and this is a machine-facing field; a desk client
/// resolves the barcode first through the copies endpoint.
/// </para>
/// </remarks>
public sealed record IssueLoanRequest
{
    public int BookCopyId { get; init; }

    public int MemberId { get; init; }
}

/// <summary>Payload for taking a copy back.</summary>
/// <remarks>
/// <c>returnedAt</c> is deliberately not accepted: the return time decides the
/// fine, and a caller who can set it can set it to the due date.
/// </remarks>
public sealed record ReturnLoanRequest
{
    /// <summary>
    /// Optional updated condition, recorded at the desk on inspection. A copy
    /// returned in <c>Poor</c> condition goes to <c>Damaged</c> rather than back
    /// onto the shelf.
    /// </summary>
    public CopyCondition? Condition { get; init; }
}

/// <summary>Payload for extending a loan.</summary>
public sealed record RenewLoanRequest
{
    /// <summary>
    /// Extra days. Optional — defaults to the member's usual loan period, which is
    /// what "renew" means at a desk.
    /// </summary>
    public int? AdditionalDays { get; init; }
}

/// <summary>Filter, sort and paging options for the loan listing.</summary>
public sealed record LoanSearchRequest : PageRequest
{
    /// <summary>Free text across barcode, book title, member name and membership number.</summary>
    public string? Search { get; init; }

    public int? MemberId { get; init; }

    public int? BookCopyId { get; init; }

    public int? BookId { get; init; }

    /// <summary>
    /// Active, Overdue or Returned. Resolved against the clock, not a column.
    /// </summary>
    public LoanStatus? Status { get; init; }

    /// <summary>Loans issued on or after this instant.</summary>
    public DateTimeOffset? IssuedFrom { get; init; }

    public DateTimeOffset? IssuedTo { get; init; }

    /// <summary>Loans due on or after this instant.</summary>
    public DateTimeOffset? DueFrom { get; init; }

    public DateTimeOffset? DueTo { get; init; }

    /// <summary>Only loans that are out and past due. Shorthand for the overdue report.</summary>
    public bool OverdueOnly { get; init; }

    /// <summary>One of <see cref="LoanSortOptions.AllowedFields"/>.</summary>
    public string? SortBy { get; init; }

    /// <summary><c>asc</c> or <c>desc</c>.</summary>
    public string? SortDir { get; init; }

    public bool IsDescending =>
        string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Payload for settling a fine.</summary>
public sealed record PayFineRequest
{
    /// <summary>
    /// Optional note — a receipt number, or "paid in cash at the desk".
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>Payload for cancelling a fine.</summary>
/// <remarks>
/// The reason is required by the entity as well as this validator. Waiving money
/// owed is precisely the operation that needs to be reviewable afterwards.
/// </remarks>
public sealed record WaiveFineRequest
{
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Filter and paging options for the fine listing.</summary>
public sealed record FineSearchRequest : PageRequest
{
    public int? MemberId { get; init; }

    /// <summary>True for unpaid and unwaived only; false for settled only.</summary>
    public bool? Outstanding { get; init; }

    public DateTimeOffset? AssessedFrom { get; init; }

    public DateTimeOffset? AssessedTo { get; init; }
}
