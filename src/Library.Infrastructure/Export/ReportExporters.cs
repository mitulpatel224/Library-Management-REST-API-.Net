using System.Text;
using System.Text.Json;
using Library.Application.Reports.Export;
using Library.Domain.Exceptions;

namespace Library.Infrastructure.Export;

/// <summary>
/// Writes a report as CSV, streaming and with formula injection neutralised.
/// </summary>
/// <remarks>
/// <para>
/// Rows are written as they arrive. The <see cref="StreamWriter"/> buffers a few
/// KB and flushes; nothing accumulates, so a 200,000-row export uses the same
/// memory as a 10-row one.
/// </para>
/// <para>
/// A UTF-8 BOM is written on purpose — see <see cref="Utf8WithBom"/>.
/// </para>
/// </remarks>
public sealed class CsvReportExporter : IReportExporter
{
    /// <summary>
    /// UTF-8 <b>with</b> a byte-order mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A BOM is unwanted in most modern contexts, and required here. Excel on
    /// Windows opens a BOM-less CSV as the system ANSI code page, so a member
    /// named "Fatima Khan" is fine but "José" arrives as "JosÃ©". The BOM is what
    /// tells Excel the file is UTF-8.
    /// </para>
    /// <para>
    /// The trade: some strict parsers surface the BOM as stray characters on the
    /// first header. Given these files are opened in a spreadsheet far more often
    /// than parsed by a script, mangled names are the worse failure.
    /// </para>
    /// </remarks>
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly ICsvFieldEncoder _encoder;

    public CsvReportExporter(ICsvFieldEncoder encoder) => _encoder = encoder;

    public ExportFormat Format => ExportFormat.Csv;

    public string ContentType => "text/csv";

    public string FileExtension => ".csv";

    public async Task WriteAsync<T>(
        Stream destination,
        IAsyncEnumerable<T> rows,
        IReadOnlyList<string>? headers = null,
        CancellationToken cancellationToken = default)
        where T : IExportableRow
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        // leaveOpen: the destination is the HTTP response body and the framework
        // owns its lifetime. Disposing it here truncates the response.
        await using var writer = new StreamWriter(destination, Utf8WithBom, leaveOpen: true);

        // Headers come from the row TYPE, or from the caller for a report whose
        // columns depend on the request - never from a row. That is what lets an
        // empty report still produce a valid file with its header, which is what
        // a consumer parsing it expects.
        await writer.WriteLineAsync(
            string.Join(',', (headers ?? T.GetHeaders()).Select(h => _encoder.Encode(h))))
            .ConfigureAwait(false);

        await foreach (T row in rows.WithCancellation(cancellationToken))
        {
            string line = string.Join(',', row.GetValues().Select(v => _encoder.Encode(v)));

            // WriteLineAsync with the token, so an abandoned download stops
            // writing rather than running the query to completion for a client
            // that has gone.
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Writes a report as a JSON array, streaming.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="Utf8JsonWriter"/> over the response body rather than
/// serialising a list. The array is opened, each row written as it arrives, and
/// the array closed — so the whole set is never held at once.
/// </para>
/// <para>
/// <b>No formula escaping here, and that is correct.</b> The injection is an
/// attack on spreadsheet software opening a CSV. JSON is not opened by a
/// spreadsheet, and prefixing values with an apostrophe would corrupt the data
/// for every legitimate consumer. A defence applied where the threat does not
/// exist is just a bug.
/// </para>
/// </remarks>
public sealed class JsonReportExporter : IReportExporter
{
    public ExportFormat Format => ExportFormat.Json;

    public string ContentType => "application/json";

    public string FileExtension => ".json";

    public async Task WriteAsync<T>(
        Stream destination,
        IAsyncEnumerable<T> rows,
        IReadOnlyList<string>? headers = null,
        CancellationToken cancellationToken = default)
        where T : IExportableRow
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);

        await using var writer = new Utf8JsonWriter(
            destination, new JsonWriterOptions { Indented = false, SkipValidation = false });

        // The property NAMES of every object in the array, so a variable column
        // set changes the JSON shape exactly as it changes the CSV columns.
        IReadOnlyList<string> names = headers ?? T.GetHeaders();

        writer.WriteStartArray();

        await foreach (T row in rows.WithCancellation(cancellationToken))
        {
            IReadOnlyList<ExportValue> values = row.GetValues();

            writer.WriteStartObject();

            for (int i = 0; i < names.Count && i < values.Count; i++)
            {
                // Every value is written as a JSON string, so the "is this cell a
                // number?" ambiguity that ExportValue.IsText exists to settle
                // never arises here. The declaration is correctly ignored.
                writer.WriteString(names[i], values[i].Value ?? string.Empty);
            }

            writer.WriteEndObject();

            // Flushed periodically so the client sees progress and the writer's
            // internal buffer does not grow with the result set. Without this the
            // "streaming" claim is only half true.
            if (writer.BytesPending > 16 * 1024)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Selects the exporter registered for a format.</summary>
public sealed class ReportExporterFactory : IReportExporterFactory
{
    private readonly Dictionary<ExportFormat, IReportExporter> _exporters;

    public ReportExporterFactory(IEnumerable<IReportExporter> exporters)
    {
        ArgumentNullException.ThrowIfNull(exporters);

        _exporters = exporters.ToDictionary(e => e.Format);
    }

    public IReportExporter GetExporter(ExportFormat format)
    {
        return _exporters.TryGetValue(format, out IReportExporter? exporter)
            ? exporter
            : throw new BusinessRuleViolationException(
                "report.unsupported_format",
                $"No exporter is registered for the '{format}' format.");
    }
}
