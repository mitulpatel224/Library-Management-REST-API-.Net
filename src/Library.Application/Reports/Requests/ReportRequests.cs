using Library.Application.Reports.Dtos;
using Library.Application.Reports.Export;
using Library.Domain.Enums;

namespace Library.Application.Reports.Requests;

/// <summary>
/// Options shared by every exportable report.
/// </summary>
/// <remarks>
/// Note the absence of <c>page</c> and <c>pageSize</c>. A report is a complete
/// answer by definition — a paged export is not an export, it is a listing, and
/// the listing endpoints already exist for that. The protection against an
/// enormous result is streaming rather than truncation.
/// </remarks>
public abstract record ExportRequest
{
    /// <summary><c>csv</c> or <c>json</c>. Defaults to CSV.</summary>
    public ExportFormat Format { get; init; } = ExportFormat.Csv;
}

/// <summary>Filters for the catalogue export.</summary>
public sealed record BookReportRequest : ExportRequest
{
    public int? CategoryId { get; init; }

    public int? PublisherId { get; init; }

    /// <summary>When true, only titles with at least one copy on the shelf.</summary>
    public bool AvailableOnly { get; init; }
}

/// <summary>Filters for the lending report.</summary>
public sealed record LoanReportRequest : ExportRequest
{
    /// <summary>Loans issued on or after this date.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Loans issued on or before this date.</summary>
    public DateOnly? To { get; init; }

    public LoanStatus? Status { get; init; }

    public int? MemberId { get; init; }
}

/// <summary>Filters and optional columns for the membership export.</summary>
/// <remarks>
/// <para>
/// The three <c>include*</c> flags default to <c>false</c>, so the unparameterised
/// export is the membership roll and nothing more. A report that always carried
/// every aggregate would be wider than most callers want and would make the
/// interesting columns harder to find.
/// </para>
/// <para>
/// They control the <b>file</b>, not the query — see
/// <c>ReportRepository.StreamMembersAsync</c>.
/// </para>
/// </remarks>
public sealed record MemberReportRequest : ExportRequest
{
    /// <summary>Only members in this state. All states when omitted.</summary>
    public MemberStatus? Status { get; init; }

    public int? MembershipTypeId { get; init; }

    /// <summary>Adds <c>booksBorrowed</c>: loans ever taken, returned ones included.</summary>
    public bool IncludeBookCounts { get; init; }

    /// <summary>Adds <c>activeLoans</c>: copies the member is holding right now.</summary>
    public bool IncludeActiveLoans { get; init; }

    /// <summary>
    /// Adds <c>totalFines</c> and <c>outstandingFines</c>.
    /// </summary>
    /// <remarks>
    /// Two columns for one flag, because the lifetime total cannot answer the
    /// question a librarian actually asks — "who owes us money?" — and the
    /// outstanding figure alone loses the history.
    /// </remarks>
    public bool IncludeFines { get; init; }

    /// <summary>The optional columns this request selects.</summary>
    public MemberExportColumns Columns => new()
    {
        BookCounts = IncludeBookCounts,
        ActiveLoans = IncludeActiveLoans,
        Fines = IncludeFines,
    };
}

/// <summary>Options for the overdue report.</summary>
public sealed record OverdueReportRequest : ExportRequest
{
    /// <summary>
    /// The date to evaluate "overdue" against. Defaults to today.
    /// </summary>
    /// <remarks>
    /// Settable because the question "who was overdue on the 1st?" is a real one
    /// — it is how a librarian reconstructs what a chase letter should have said.
    /// Being able to ask it of a past date also makes the report testable without
    /// waiting for a loan to age.
    /// </remarks>
    public DateOnly? AsOf { get; init; }
}

/// <summary>Period for the fine summary.</summary>
public sealed record FineSummaryRequest
{
    /// <summary>Defaults to the start of the current year.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Defaults to today.</summary>
    public DateOnly? To { get; init; }
}
