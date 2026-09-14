namespace Library.Application.Reports.Export;

/// <summary>The formats a report can be exported in.</summary>
public enum ExportFormat
{
    Csv = 0,
    Json = 1,
}

/// <summary>
/// Writes a sequence of rows to a stream in one export format.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a stream rather than a returned string or byte array.</b> Building the
/// whole export in memory first means a 200,000-row loan report allocates the
/// entire file before a byte reaches the client. Writing into the response body
/// as rows arrive keeps memory roughly constant and lets the download start
/// immediately — the same reasoning as the import reader, in the other
/// direction.
/// </para>
/// <para>
/// <b>Liskov, concretely.</b> Both implementations honour the same contract:
/// consume the sequence once, write to the stream, never buffer the whole set,
/// never dispose the stream. A caller swaps CSV for JSON by changing an enum and
/// nothing else — including the streaming guarantee, which an implementation that
/// quietly buffered would violate while still compiling.
/// </para>
/// </remarks>
public interface IReportExporter
{
    /// <summary>The format this exporter produces.</summary>
    ExportFormat Format { get; }

    /// <summary>The MIME type to set on the response.</summary>
    string ContentType { get; }

    /// <summary>The file extension, including the dot.</summary>
    string FileExtension { get; }

    /// <summary>
    /// Writes <paramref name="rows"/> to <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Must not dispose <paramref name="destination"/> — it is the response body,
    /// and the framework owns its lifetime. Must not enumerate
    /// <paramref name="rows"/> more than once; it is a live database read.
    /// </remarks>
    Task WriteAsync<T>(
        Stream destination,
        IAsyncEnumerable<T> rows,
        CancellationToken cancellationToken = default)
        where T : IExportableRow;
}

/// <summary>
/// Implemented by any row type that can be exported.
/// </summary>
/// <remarks>
/// <para>
/// The row describes its own header and values, which keeps the exporters
/// completely generic — neither of them knows what a book or a loan is.
/// </para>
/// <para>
/// <b>Why not reflection over properties.</b> It would remove this interface, at
/// three costs: column order becomes whatever the runtime reports, renaming a
/// property silently renames an exported column that someone's script depends
/// on, and formatting a <c>DateTimeOffset</c> or a decimal correctly needs
/// per-property knowledge the reflection path does not have. An explicit method
/// is more typing and no surprises.
/// </para>
/// </remarks>
public interface IExportableRow
{
    /// <summary>Column headers, in order. Must align with <see cref="GetValues"/>.</summary>
    static abstract IReadOnlyList<string> GetHeaders();

    /// <summary>This row's values, in the same order as the headers.</summary>
    IReadOnlyList<string?> GetValues();
}

/// <summary>Selects the exporter registered for a format.</summary>
public interface IReportExporterFactory
{
    /// <exception cref="Domain.Exceptions.BusinessRuleViolationException">
    /// No exporter is registered for that format.
    /// </exception>
    IReportExporter GetExporter(ExportFormat format);
}
