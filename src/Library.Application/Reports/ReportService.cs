using Library.Application.Common.Abstractions;
using Library.Application.Loans;
using Library.Application.Reports.Dtos;
using Library.Application.Reports.Export;
using Library.Application.Reports.Requests;
using Library.Domain.Exceptions;

namespace Library.Application.Reports;

/// <summary>Reporting and export use cases.</summary>
public interface IReportService
{
    /// <summary>Streams the catalogue to <paramref name="destination"/>.</summary>
    Task<ExportDescriptor> ExportBooksAsync(
        Stream destination,
        BookReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams the lending report to <paramref name="destination"/>.</summary>
    Task<ExportDescriptor> ExportLoansAsync(
        Stream destination,
        LoanReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams the membership report to <paramref name="destination"/>.</summary>
    Task<ExportDescriptor> ExportMembersAsync(
        Stream destination,
        MemberReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Streams the overdue report to <paramref name="destination"/>.</summary>
    Task<ExportDescriptor> ExportOverdueAsync(
        Stream destination,
        OverdueReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Aggregated fine totals for a period.</summary>
    Task<FineSummaryDto> GetFineSummaryAsync(
        FineSummaryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves the content type and filename for a format, without writing.</summary>
    ExportDescriptor DescribeExport(string reportName, ExportFormat format);
}

/// <summary>What the caller needs to set on the HTTP response.</summary>
/// <remarks>
/// Returned rather than having the service touch <c>HttpResponse</c> directly,
/// which would drag ASP.NET Core into the Application layer. The service knows
/// the MIME type and filename; the controller knows how to put them on a
/// response.
/// </remarks>
public sealed record ExportDescriptor
{
    public string ContentType { get; init; } = null!;

    public string FileName { get; init; } = null!;
}

/// <inheritdoc cref="IReportService"/>
public sealed class ReportService : IReportService
{
    private readonly IReportRepository _repository;
    private readonly IReportExporterFactory _exporters;
    private readonly FineRateResolver _fineRate;
    private readonly FineCurrencyResolver _currency;
    private readonly IClock _clock;

    public ReportService(
        IReportRepository repository,
        IReportExporterFactory exporters,
        FineRateResolver fineRate,
        FineCurrencyResolver currency,
        IClock clock)
    {
        _repository = repository;
        _exporters = exporters;
        _fineRate = fineRate;
        _currency = currency;
        _clock = clock;
    }

    public async Task<ExportDescriptor> ExportBooksAsync(
        Stream destination,
        BookReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReportExporter exporter = _exporters.GetExporter(request.Format);

        // The enumerable is not consumed here - it is handed to the exporter,
        // which pulls rows as it writes them. Nothing is materialised.
        await exporter.WriteAsync(
            destination,
            _repository.StreamBooksAsync(request, cancellationToken),
            cancellationToken: cancellationToken);

        return Describe("books", exporter);
    }

    public async Task<ExportDescriptor> ExportMembersAsync(
        Stream destination,
        MemberReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReportExporter exporter = _exporters.GetExporter(request.Format);

        // The only export whose columns depend on the request, so the headers are
        // passed explicitly rather than taken from the row type. Resolved here,
        // before a single row is read - the exporter still writes a header line
        // for an empty membership.
        await exporter.WriteAsync(
            destination,
            _repository.StreamMembersAsync(request, cancellationToken),
            request.Columns.Headers(),
            cancellationToken);

        return Describe("members", exporter);
    }

    public async Task<ExportDescriptor> ExportLoansAsync(
        Stream destination,
        LoanReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateRange(request.From, request.To);

        IReportExporter exporter = _exporters.GetExporter(request.Format);

        await exporter.WriteAsync(
            destination,
            _repository.StreamLoansAsync(request, _clock.UtcNow, cancellationToken),
            cancellationToken: cancellationToken);

        return Describe("loans", exporter);
    }

    public async Task<ExportDescriptor> ExportOverdueAsync(
        Stream destination,
        OverdueReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReportExporter exporter = _exporters.GetExporter(request.Format);

        // asOf is a DATE, converted to the end of that day so a loan due at
        // 09:00 on the 5th counts as overdue when asking about the 5th. Taking
        // midnight instead would report it as not yet due for the whole day it
        // was actually late.
        DateTimeOffset asOf = request.AsOf is null
            ? _clock.UtcNow
            : new DateTimeOffset(
                request.AsOf.Value.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        await exporter.WriteAsync(
            destination,
            _repository.StreamOverdueAsync(asOf, _fineRate(), cancellationToken),
            cancellationToken: cancellationToken);

        return Describe("overdue", exporter);
    }

    public Task<FineSummaryDto> GetFineSummaryAsync(
        FineSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateRange(request.From, request.To);

        // Defaults chosen so an unparameterised call answers the question a
        // librarian usually means: "how are we doing this year?"
        FineSummaryRequest normalized = request with
        {
            From = request.From ?? new DateOnly(_clock.Today.Year, 1, 1),
            To = request.To ?? _clock.Today,
        };

        return _repository.GetFineSummaryAsync(normalized, _currency(), cancellationToken);
    }

    public ExportDescriptor DescribeExport(string reportName, ExportFormat format) =>
        Describe(reportName, _exporters.GetExporter(format));

    /// <summary>
    /// Builds the filename, stamped with the date the report was produced.
    /// </summary>
    /// <remarks>
    /// A librarian running the overdue report weekly ends up with a folder of
    /// downloads. <c>overdue.csv (3)</c> is unusable; <c>overdue-2026-09-14.csv</c>
    /// sorts and identifies itself.
    /// </remarks>
    private ExportDescriptor Describe(string reportName, IReportExporter exporter) => new()
    {
        ContentType = exporter.ContentType,
        FileName = $"{reportName}-{_clock.Today:yyyy-MM-dd}{exporter.FileExtension}",
    };

    /// <summary>
    /// Rejects an inverted date range.
    /// </summary>
    /// <remarks>
    /// <c>from=2030-01-01&amp;to=2000-01-01</c> matches nothing, so the empty
    /// report it would otherwise produce is indistinguishable from "nothing
    /// happened in that period" — a real answer to a question nobody asked.
    /// There is no sensible value to clamp to, so it is refused. The same rule the
    /// member listing applies.
    /// </remarks>
    private static void ValidateRange(DateOnly? from, DateOnly? to)
    {
        if (from is not null && to is not null && to < from)
        {
            throw new BusinessRuleViolationException(
                "report.invalid_date_range",
                $"'to' ({to:yyyy-MM-dd}) cannot be earlier than 'from' ({from:yyyy-MM-dd}).");
        }
    }
}
