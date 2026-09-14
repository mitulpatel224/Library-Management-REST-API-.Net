using Library.Application.Reports.Dtos;
using Library.Application.Reports.Requests;

namespace Library.Application.Reports;

/// <summary>
/// Read access for reporting.
/// </summary>
/// <remarks>
/// <para>
/// <b>The export methods return <see cref="IAsyncEnumerable{T}"/>, not
/// <see cref="List{T}"/>.</b> This is the one place the "no <c>IQueryable</c>
/// escapes the repository" rule is stretched, and the reason is worth stating:
/// a report is unbounded. A catalogue export of 200,000 books materialised into a
/// list allocates every row before the first byte is sent.
/// </para>
/// <para>
/// <c>IAsyncEnumerable</c> keeps the streaming without leaking composability —
/// the caller can enumerate it but cannot append a <c>Where</c> and change the
/// query. The abstraction that mattered is preserved; the buffering is not.
/// </para>
/// <para>
/// The summary method returns a materialised DTO, because an aggregate is a
/// single row by construction.
/// </para>
/// </remarks>
public interface IReportRepository
{
    /// <summary>Streams the whole catalogue, ordered by title.</summary>
    IAsyncEnumerable<BookExportRow> StreamBooksAsync(
        BookReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams loans in a date range, with status computed against <c>asOf</c>.</summary>
    IAsyncEnumerable<LoanExportRow> StreamLoansAsync(
        LoanReportRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the membership roll, ordered by name, with loan and fine
    /// aggregates computed in SQL.
    /// </summary>
    IAsyncEnumerable<MemberExportRow> StreamMembersAsync(
        MemberReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams loans still out and past their due date at <c>asOf</c>.</summary>
    IAsyncEnumerable<OverdueExportRow> StreamOverdueAsync(
        DateTimeOffset asOf,
        decimal ratePerDay,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates fine totals for a period.
    /// </summary>
    /// <remarks>
    /// Every figure is computed by the database. Summing in memory would return
    /// the same answer and transfer the whole table to do it.
    /// </remarks>
    Task<FineSummaryDto> GetFineSummaryAsync(
        FineSummaryRequest request,
        string currency,
        CancellationToken cancellationToken = default);
}
