using Library.Domain.Enums;

namespace Library.Application.Members.Dtos;

/// <summary>Lightweight projection for member list and search results.</summary>
public sealed record MemberSummaryDto
{
    public int Id { get; init; }

    /// <summary>Human-facing identifier, e.g. <c>MEM-2026-00042</c>.</summary>
    public string MembershipNumber { get; init; } = null!;

    public string FullName { get; init; } = null!;

    public string Email { get; init; } = null!;

    public string? Phone { get; init; }

    public string MembershipTypeName { get; init; } = null!;

    public MemberStatus Status { get; init; }

    public DateOnly JoinedOn { get; init; }

    /// <summary>
    /// Whether this member may borrow right now.
    /// </summary>
    /// <remarks>
    /// Surfaced on the summary so a librarian's list shows eligibility without a
    /// second call per row — the desk needs to know before scanning a book, not
    /// after.
    /// </remarks>
    public bool CanBorrow { get; init; }
}

/// <summary>Full detail for a single member.</summary>
public sealed record MemberDetailDto
{
    public int Id { get; init; }

    public string MembershipNumber { get; init; } = null!;

    public string FullName { get; init; } = null!;

    public string Email { get; init; } = null!;

    public string? Phone { get; init; }

    public string? Address { get; init; }

    public int MembershipTypeId { get; init; }

    public string MembershipTypeName { get; init; } = null!;

    /// <summary>Copies this member may hold at once, from their membership type.</summary>
    public int MaxConcurrentLoans { get; init; }

    /// <summary>Days from issue to due date, from their membership type.</summary>
    public int LoanPeriodDays { get; init; }

    public MemberStatus Status { get; init; }

    /// <summary>Why the member was suspended. Null unless suspended.</summary>
    public string? StatusReason { get; init; }

    public bool CanBorrow { get; init; }

    public DateOnly JoinedOn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record MembershipTypeDto
{
    public int Id { get; init; }

    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    public int MaxConcurrentLoans { get; init; }

    public int LoanPeriodDays { get; init; }

    /// <summary>How many members hold this type. Counted in SQL, not in memory.</summary>
    public int MemberCount { get; init; }
}
