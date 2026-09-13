using Library.Application.Common.Models;
using Library.Domain.Enums;

namespace Library.Application.Members.Requests;

/// <summary>Filter, sort and paging options for the member listing.</summary>
public sealed record MemberSearchRequest : PageRequest
{
    /// <summary>Free text across name, membership number and email.</summary>
    public string? Search { get; init; }

    public MemberStatus? Status { get; init; }

    public int? MembershipTypeId { get; init; }

    /// <summary>Members who joined on or after this date.</summary>
    public DateOnly? JoinedFrom { get; init; }

    public DateOnly? JoinedTo { get; init; }

    /// <summary>
    /// When true, returns only members currently permitted to borrow.
    /// </summary>
    /// <remarks>
    /// A convenience over <c>status=Active</c> that survives the eligibility rule
    /// getting more complex — Phase 4 may well make it depend on unpaid fines,
    /// at which point this filter keeps meaning what it says and
    /// <c>status=Active</c> would not.
    /// </remarks>
    public bool CanBorrowOnly { get; init; }

    /// <summary>One of <see cref="MemberSortOptions.AllowedFields"/>.</summary>
    public string? SortBy { get; init; }

    /// <summary><c>asc</c> or <c>desc</c>.</summary>
    public string? SortDir { get; init; }

    public bool IsDescending =>
        string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Payload for registering a member.
/// </summary>
/// <remarks>
/// Note what is absent: <c>MembershipNumber</c> and <c>Status</c>. The number is
/// derived from the database key after insert, and status is reached only
/// through the suspend/reactivate/cancel operations. Accepting either here would
/// let a caller claim an identifier that is supposed to be issued, or grant
/// themselves borrowing rights a librarian withdrew.
/// </remarks>
public sealed record CreateMemberRequest
{
    public string FullName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    /// <summary>Optional. Accepts spaces, hyphens, dots and brackets.</summary>
    public string? Phone { get; init; }

    public string? Address { get; init; }

    public int MembershipTypeId { get; init; }

    /// <summary>Defaults to today when omitted.</summary>
    public DateOnly? JoinedOn { get; init; }
}

/// <summary>Payload for updating a member's details.</summary>
public sealed record UpdateMemberRequest
{
    public string FullName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string? Phone { get; init; }

    public string? Address { get; init; }

    /// <summary>A member may be moved between membership types — Student to Standard on graduation.</summary>
    public int MembershipTypeId { get; init; }
}

/// <summary>Payload for suspending a member.</summary>
/// <remarks>
/// The reason is required by the domain, not merely by this validator: a
/// suspension nobody can explain is one nobody can fairly lift, and the member
/// is entitled to be told why.
/// </remarks>
public sealed record SuspendMemberRequest
{
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Payload for cancelling a membership. The reason is optional.</summary>
public sealed record CancelMemberRequest
{
    public string? Reason { get; init; }
}

public sealed record CreateMembershipTypeRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int MaxConcurrentLoans { get; init; }

    public int LoanPeriodDays { get; init; }
}
