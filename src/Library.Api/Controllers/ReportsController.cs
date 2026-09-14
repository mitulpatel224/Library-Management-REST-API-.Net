using Library.Application.Reports;
using Library.Application.Reports.Dtos;
using Library.Application.Reports.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Controllers;

/// <summary>Reports and data exports.</summary>
/// <remarks>
/// <para>
/// <b>The export endpoints write straight to the response body.</b> They return
/// <see cref="EmptyResult"/> rather than a <c>FileResult</c>, and that is not a
/// shortcut — a <c>FileResult</c> takes a completed byte array or stream, which
/// means the whole report has to exist before anything is sent.
/// </para>
/// <para>
/// Writing into <c>Response.Body</c> as rows arrive keeps memory constant and
/// starts the download immediately. The cost is that headers must be set
/// <i>before</i> the first byte is written — once the response has started,
/// changing the status code is impossible, so an error mid-stream truncates the
/// file rather than producing a clean 500. That trade is right for a report and
/// wrong for almost anything else.
/// </para>
/// </remarks>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportService _reports;

    public ReportsController(IReportService reports) => _reports = reports;

    /// <summary>Exports the catalogue as CSV or JSON.</summary>
    /// <remarks>
    /// Column names match the import template, so an exported catalogue can be
    /// edited in a spreadsheet and imported straight back through
    /// <c>POST /api/books/import</c>.
    /// <para>
    /// CSV output is defended against spreadsheet formula injection: a value
    /// beginning <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is prefixed with an
    /// apostrophe so Excel treats it as text rather than executing it.
    /// </para>
    /// </remarks>
    /// <response code="200">The export, streamed as a file download.</response>
    [HttpGet("books/export", Name = "ExportBooks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportBooks(
        [FromQuery] BookReportRequest request,
        CancellationToken cancellationToken)
    {
        return await StreamAsync(
            "books", request.Format,
            (stream, ct) => _reports.ExportBooksAsync(stream, request, ct),
            cancellationToken);
    }

    /// <summary>Exports loans in a date range.</summary>
    /// <remarks>
    /// <c>from</c> and <c>to</c> filter on the <b>issue</b> date and are
    /// inclusive of both days. Status is evaluated as at now, so a loan that is
    /// currently late reports <c>Overdue</c> even if it was within its term for
    /// most of the period.
    /// </remarks>
    /// <response code="200">The export, streamed as a file download.</response>
    /// <response code="422"><c>to</c> is earlier than <c>from</c>.</response>
    [HttpGet("loans", Name = "ExportLoans")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ExportLoans(
        [FromQuery] LoanReportRequest request,
        CancellationToken cancellationToken)
    {
        return await StreamAsync(
            "loans", request.Format,
            (stream, ct) => _reports.ExportLoansAsync(stream, request, ct),
            cancellationToken);
    }

    /// <summary>Exports loans still out and past their due date.</summary>
    /// <remarks>
    /// <para>
    /// Carries each member's email and phone, because the purpose of this report
    /// is to contact them.
    /// </para>
    /// <para>
    /// <c>asOf</c> defaults to today but accepts a past date, which answers "who
    /// was overdue on the 1st?" — how a librarian reconstructs what a chase
    /// letter should have said.
    /// </para>
    /// <para>
    /// <c>projectedFine</c> is what the fine <i>would</i> be if the copy came back
    /// on <c>asOf</c>. Nothing is charged until the copy is actually returned.
    /// </para>
    /// </remarks>
    /// <response code="200">The export, streamed as a file download.</response>
    [HttpGet("overdue", Name = "ExportOverdue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportOverdue(
        [FromQuery] OverdueReportRequest request,
        CancellationToken cancellationToken)
    {
        return await StreamAsync(
            "overdue", request.Format,
            (stream, ct) => _reports.ExportOverdueAsync(stream, request, ct),
            cancellationToken);
    }

    /// <summary>Aggregated fine totals for a period.</summary>
    /// <remarks>
    /// <para>
    /// Returns JSON rather than a file — this is a dashboard figure, not a data
    /// dump, and every total is computed by the database.
    /// </para>
    /// <para>
    /// Defaults to the current year to date when no range is supplied.
    /// </para>
    /// </remarks>
    /// <response code="200">The summary.</response>
    /// <response code="422"><c>to</c> is earlier than <c>from</c>.</response>
    [HttpGet("fines/summary", Name = "GetFineSummary")]
    [Produces("application/json")]
    [ProducesResponseType<FineSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FineSummaryDto>> GetFineSummary(
        [FromQuery] FineSummaryRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _reports.GetFineSummaryAsync(request, cancellationToken));
    }

    /// <summary>
    /// Sets the download headers, then streams the report into the response body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order matters here.</b> <c>ContentType</c> and
    /// <c>Content-Disposition</c> are set before the writer touches the body,
    /// because headers cannot be changed once the response has started. The
    /// descriptor is resolved up front for exactly that reason — asking the
    /// service for the filename after writing would be too late.
    /// </para>
    /// <para>
    /// The filename is quoted in the header. Without quotes a name containing a
    /// space or a comma is parsed as multiple header parameters, and the browser
    /// saves the file under a truncated name.
    /// </para>
    /// </remarks>
    private async Task<IActionResult> StreamAsync(
        string reportName,
        Application.Reports.Export.ExportFormat format,
        Func<Stream, CancellationToken, Task<ExportDescriptor>> write,
        CancellationToken cancellationToken)
    {
        ExportDescriptor descriptor = _reports.DescribeExport(reportName, format);

        Response.ContentType = descriptor.ContentType;
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{descriptor.FileName}\"";

        await write(Response.Body, cancellationToken);

        // The response has already been written. EmptyResult tells MVC there is
        // nothing further to add - returning Ok() here would attempt to serialise
        // a body onto a stream that is already complete.
        return new EmptyResult();
    }
}
