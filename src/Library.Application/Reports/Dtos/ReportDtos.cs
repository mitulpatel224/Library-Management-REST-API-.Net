using System.Globalization;
using Library.Application.Reports.Export;

namespace Library.Application.Reports.Dtos;

/// <summary>
/// Formatting shared by every exported row.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="CultureInfo.InvariantCulture"/> everywhere, deliberately.</b> A
/// report formatted in the server's culture is a file whose meaning depends on
/// where it was generated: <c>1,234.50</c> in one locale and <c>1.234,50</c> in
/// another, both written to a column someone's script parses as a number.
/// </para>
/// <para>
/// Dates are ISO 8601 for the same reason, and one more: <c>03/04/2024</c> is
/// March in the US and April in the UK, with nothing in the file to say which.
/// </para>
/// </remarks>
internal static class ExportFormatting
{
    public static string Date(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Instant(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Two decimal places, no thousands separator, no currency symbol.</summary>
    /// <remarks>
    /// A symbol would make the column non-numeric to every consumer. Currency is
    /// a property of the whole report, not of each cell, and is stated in
    /// <c>FineOptions</c>.
    /// </remarks>
    public static string Money(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One book, as exported by the catalogue report.</summary>
public sealed record BookExportRow : IExportableRow
{
    public int Id { get; init; }

    public string Isbn { get; init; } = null!;

    public string Title { get; init; } = null!;

    public string? Subtitle { get; init; }

    public string CategoryName { get; init; } = null!;

    public string? PublisherName { get; init; }

    public DateOnly? PublishedOn { get; init; }

    public string? Language { get; init; }

    public int? PageCount { get; init; }

    /// <summary>Semicolon-separated, in credit order — the same shape the importer reads.</summary>
    public string Authors { get; init; } = string.Empty;

    public string Genres { get; init; } = string.Empty;

    public int TotalCopies { get; init; }

    public int AvailableCopies { get; init; }

    /// <remarks>
    /// Headers match the import template's column names, so an exported
    /// catalogue can be edited and imported straight back. A round trip that
    /// needs a column rename in between is one nobody performs.
    /// </remarks>
    public static IReadOnlyList<string> GetHeaders() =>
    [
        "id", "isbn", "title", "subtitle", "category", "publisher",
        "publishedOn", "language", "pageCount", "authors", "genres",
        "totalCopies", "availableCopies",
    ];

    public IReadOnlyList<string?> GetValues() =>
    [
        ExportFormatting.Number(Id),
        Isbn,
        Title,
        Subtitle,
        CategoryName,
        PublisherName,
        ExportFormatting.Date(PublishedOn),
        Language,
        PageCount is null ? string.Empty : ExportFormatting.Number(PageCount.Value),
        Authors,
        Genres,
        ExportFormatting.Number(TotalCopies),
        ExportFormatting.Number(AvailableCopies),
    ];
}

/// <summary>One loan, as exported by the lending report.</summary>
public sealed record LoanExportRow : IExportableRow
{
    public int Id { get; init; }

    public string Barcode { get; init; } = null!;

    public string Isbn { get; init; } = null!;

    public string Title { get; init; } = null!;

    public string MembershipNumber { get; init; } = null!;

    public string MemberName { get; init; } = null!;

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public DateTimeOffset? ReturnedAt { get; init; }

    /// <summary>The instant this row was evaluated against.</summary>
    /// <remarks>
    /// Projected onto the row so the derived values below can be computed
    /// without a second pass — see <see cref="DaysOverdue"/>.
    /// </remarks>
    public DateTimeOffset AsOf { get; init; }

    /// <summary>Computed against <see cref="AsOf"/>, not against "now".</summary>
    public string Status { get; init; } = null!;

    /// <summary>
    /// Whole days late, floored at zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Computed here in C#, not in SQL, and that was a bug fix.</b> The first
    /// version used <c>EF.Functions.DateDiffDay</c>, which is a SQL Server
    /// function. The SQLite provider cannot translate it, so the query silently
    /// switched to client evaluation and then threw <i>mid-stream</i> — after the
    /// first row had already been written to the response, truncating the
    /// download instead of failing cleanly.
    /// </para>
    /// <para>
    /// That is exactly the failure the dual-provider rule exists to catch: code
    /// that works on one database and not the other. The date <i>comparisons</i>
    /// in the filters translate everywhere; only the day arithmetic did not, and
    /// it is O(1) per row, so doing it as the row materialises costs nothing and
    /// keeps the streaming intact.
    /// </para>
    /// </remarks>
    public int DaysOverdue
    {
        get
        {
            DateTimeOffset endpoint = ReturnedAt ?? AsOf;

            return endpoint <= DueAt ? 0 : (int)(endpoint.Date - DueAt.Date).TotalDays;
        }
    }

    public decimal FineAmount { get; init; }

    public bool FineSettled { get; init; }

    public static IReadOnlyList<string> GetHeaders() =>
    [
        "loanId", "barcode", "isbn", "title", "membershipNumber", "memberName",
        "issuedAt", "dueAt", "returnedAt", "status", "daysOverdue",
        "fineAmount", "fineSettled",
    ];

    public IReadOnlyList<string?> GetValues() =>
    [
        ExportFormatting.Number(Id),
        Barcode,
        Isbn,
        Title,
        MembershipNumber,
        MemberName,
        ExportFormatting.Instant(IssuedAt),
        ExportFormatting.Instant(DueAt),
        ExportFormatting.Instant(ReturnedAt),
        Status,
        ExportFormatting.Number(DaysOverdue),
        ExportFormatting.Money(FineAmount),
        FineSettled ? "true" : "false",
    ];
}

/// <summary>One overdue loan, as exported by the overdue report.</summary>
public sealed record OverdueExportRow : IExportableRow
{
    public int LoanId { get; init; }

    public string Barcode { get; init; } = null!;

    public string Title { get; init; } = null!;

    public string MembershipNumber { get; init; } = null!;

    public string MemberName { get; init; } = null!;

    public string? MemberEmail { get; init; }

    public string? MemberPhone { get; init; }

    public DateTimeOffset DueAt { get; init; }

    /// <summary>The instant this row was evaluated against.</summary>
    public DateTimeOffset AsOf { get; init; }

    /// <summary>The fine rate in force, per overdue day.</summary>
    public decimal RatePerDay { get; init; }

    /// <summary>Whole days past the due date at <see cref="AsOf"/>.</summary>
    /// <remarks>
    /// Computed in C# rather than SQL for the same reason as
    /// <see cref="LoanExportRow.DaysOverdue"/> — the SQL Server day-difference
    /// function has no SQLite equivalent, and using it broke the export
    /// mid-stream.
    /// </remarks>
    public int DaysOverdue =>
        AsOf <= DueAt ? 0 : (int)(AsOf.Date - DueAt.Date).TotalDays;

    /// <summary>What the fine would be if the copy came back on the <c>asOf</c> date.</summary>
    /// <remarks>
    /// Projected, not charged. The fine is only assessed on return, so this is
    /// the figure a librarian quotes when chasing the loan — and it grows every
    /// day the copy stays out.
    /// </remarks>
    public decimal ProjectedFine => DaysOverdue * RatePerDay;

    public static IReadOnlyList<string> GetHeaders() =>
    [
        "loanId", "barcode", "title", "membershipNumber", "memberName",
        "memberEmail", "memberPhone", "dueAt", "daysOverdue", "projectedFine",
    ];

    public IReadOnlyList<string?> GetValues() =>
    [
        ExportFormatting.Number(LoanId),
        Barcode,
        Title,
        MembershipNumber,
        MemberName,
        MemberEmail,
        MemberPhone,
        ExportFormatting.Instant(DueAt),
        ExportFormatting.Number(DaysOverdue),
        ExportFormatting.Money(ProjectedFine),
    ];
}

/// <summary>
/// Aggregated fine totals for a period.
/// </summary>
/// <remarks>
/// Every figure here is computed by the database. Loading the fine rows and
/// summing them in C# would return the correct answer and transfer the entire
/// table to do it — the classic way a report that works on seed data dies on
/// production volumes.
/// </remarks>
public sealed record FineSummaryDto
{
    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public int TotalFines { get; init; }

    public decimal TotalAssessed { get; init; }

    public decimal TotalPaid { get; init; }

    public decimal TotalWaived { get; init; }

    /// <summary>Assessed but neither paid nor waived — what the library is still owed.</summary>
    public decimal TotalOutstanding { get; init; }

    public int PaidCount { get; init; }

    public int WaivedCount { get; init; }

    public int OutstandingCount { get; init; }

    /// <summary>Total overdue days across every fine in the period.</summary>
    public int TotalOverdueDays { get; init; }

    /// <summary>Currency the amounts are expressed in, from <c>FineOptions</c>.</summary>
    public string Currency { get; init; } = null!;

    public IReadOnlyList<FineSummaryByMonthDto> ByMonth { get; init; } = [];
}

/// <summary>Fine totals for one month, for a trend line.</summary>
public sealed record FineSummaryByMonthDto
{
    /// <summary><c>yyyy-MM</c>.</summary>
    public string Month { get; init; } = null!;

    public int Count { get; init; }

    public decimal Assessed { get; init; }

    public decimal Outstanding { get; init; }
}
