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

    /// <summary>
    /// An identifier: digits in it are a name, not a quantity.
    /// </summary>
    /// <remarks>
    /// Marks a column so the CSV encoder can stop a spreadsheet re-reading it as
    /// a number — the defect that turned every exported ISBN into
    /// <c>9.78E+12</c>. See <see cref="ExportValue"/>.
    /// </remarks>
    public static ExportValue Text(string? value) => ExportValue.Text(value);
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

    public IReadOnlyList<ExportValue> GetValues() =>
    [
        ExportFormatting.Number(Id),

        // Text, not a number. Thirteen digits is a number to Excel, which shows
        // it as 9.78E+12 - the ISBN is intact in the file and unreadable to the
        // person who opened it.
        ExportFormatting.Text(Isbn),

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

    public IReadOnlyList<ExportValue> GetValues() =>
    [
        ExportFormatting.Number(Id),

        // All three are identifiers. Only the ISBN is all digits today, so only
        // it is actually altered - the other two are declared for what they are
        // rather than for what they currently look like.
        ExportFormatting.Text(Barcode),
        ExportFormatting.Text(Isbn),

        Title,
        ExportFormatting.Text(MembershipNumber),
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

    public IReadOnlyList<ExportValue> GetValues() =>
    [
        ExportFormatting.Number(LoanId),
        ExportFormatting.Text(Barcode),
        Title,
        ExportFormatting.Text(MembershipNumber),
        MemberName,
        MemberEmail,

        // A phone number is the clearest case of digits that are not a quantity:
        // 9876543210 rendered as 9.88E+09 is a number nobody can dial, and this
        // is the one report whose purpose is to contact people.
        ExportFormatting.Text(MemberPhone),
        ExportFormatting.Instant(DueAt),
        ExportFormatting.Number(DaysOverdue),
        ExportFormatting.Money(ProjectedFine),
    ];
}

/// <summary>
/// Which optional aggregate columns a members export carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists at all.</b> Headers are produced from the row
/// <i>type</i> (<c>static abstract GetHeaders</c>), which is what lets an empty
/// report still write a valid header line. Optional columns break that: the
/// column set depends on the request, and a request is not a type.
/// </para>
/// <para>
/// The danger in a variable column set is drift — headers listing ten columns
/// while rows emit nine shifts every value one place left, and the file still
/// parses, so nothing complains. Both lists are therefore generated from this
/// one object: <see cref="Headers"/> here and
/// <see cref="MemberExportRow.GetValues"/> read the same three flags in the same
/// order. They cannot disagree without someone editing both.
/// </para>
/// </remarks>
public readonly record struct MemberExportColumns
{
    /// <summary>Adds <c>booksBorrowed</c> — loans ever taken by this member.</summary>
    public bool BookCounts { get; init; }

    /// <summary>Adds <c>activeLoans</c> — copies the member is holding right now.</summary>
    public bool ActiveLoans { get; init; }

    /// <summary>Adds <c>totalFines</c> and <c>outstandingFines</c>.</summary>
    public bool Fines { get; init; }

    /// <summary>Base columns, then whichever aggregates were asked for.</summary>
    public IReadOnlyList<string> Headers()
    {
        List<string> headers =
        [
            "memberId", "membershipNumber", "fullName", "email", "phone",
            "membershipType", "status", "joinedOn",
            "maxConcurrentLoans", "loanPeriodDays",
        ];

        if (BookCounts)
        {
            headers.Add("booksBorrowed");
        }

        if (ActiveLoans)
        {
            headers.Add("activeLoans");
        }

        if (Fines)
        {
            headers.Add("totalFines");
            headers.Add("outstandingFines");
        }

        return headers;
    }
}

/// <summary>One member, as exported by the membership report.</summary>
/// <remarks>
/// The aggregate properties are always populated — see
/// <c>ReportRepository.StreamMembersAsync</c> for why the query does not vary.
/// <see cref="Columns"/> decides which of them reach the file.
/// </remarks>
public sealed record MemberExportRow : IExportableRow
{
    public int Id { get; init; }

    public string MembershipNumber { get; init; } = null!;

    public string FullName { get; init; } = null!;

    public string Email { get; init; } = null!;

    public string? Phone { get; init; }

    public string MembershipTypeName { get; init; } = null!;

    public string Status { get; init; } = null!;

    public DateOnly JoinedOn { get; init; }

    public int MaxConcurrentLoans { get; init; }

    public int LoanPeriodDays { get; init; }

    /// <summary>Loans ever taken by this member, returned ones included.</summary>
    public int BooksBorrowed { get; init; }

    /// <summary>Copies the member is holding now — loans with no return date.</summary>
    public int ActiveLoans { get; init; }

    /// <summary>Every fine ever assessed against this member.</summary>
    public decimal TotalFines { get; init; }

    /// <summary>Assessed but neither paid nor waived — what this member still owes.</summary>
    /// <remarks>
    /// Carried alongside the lifetime total because they answer different
    /// questions. "Has this member been fined before?" is a history question;
    /// "does this member owe us money?" is the one a librarian acts on, and a
    /// lifetime total cannot answer it.
    /// </remarks>
    public decimal OutstandingFines { get; init; }

    /// <summary>Which optional columns this export carries.</summary>
    public MemberExportColumns Columns { get; init; }

    /// <summary>
    /// The base columns only.
    /// </summary>
    /// <remarks>
    /// Satisfies <see cref="IExportableRow"/>, which cannot express a
    /// request-dependent column set. The exporter is given the real header list
    /// explicitly — see <c>MemberExportColumns</c>.
    /// </remarks>
    public static IReadOnlyList<string> GetHeaders() => default(MemberExportColumns).Headers();

    public IReadOnlyList<ExportValue> GetValues()
    {
        List<ExportValue> values =
        [
            ExportFormatting.Number(Id),

            // An identifier, not a quantity. MEM-2026-00042 is safe as it stands,
            // but the column is what it is regardless of today's format.
            ExportFormatting.Text(MembershipNumber),

            FullName,
            Email,

            // The case that bites: a ten-digit phone number read as a number
            // becomes 9.88E+09, and this report exists to contact people.
            ExportFormatting.Text(Phone),

            MembershipTypeName,
            Status,
            ExportFormatting.Date(JoinedOn),
            ExportFormatting.Number(MaxConcurrentLoans),
            ExportFormatting.Number(LoanPeriodDays),
        ];

        // Same flags, same order as MemberExportColumns.Headers.
        if (Columns.BookCounts)
        {
            values.Add(ExportFormatting.Number(BooksBorrowed));
        }

        if (Columns.ActiveLoans)
        {
            values.Add(ExportFormatting.Number(ActiveLoans));
        }

        if (Columns.Fines)
        {
            values.Add(ExportFormatting.Money(TotalFines));
            values.Add(ExportFormatting.Money(OutstandingFines));
        }

        return values;
    }
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
