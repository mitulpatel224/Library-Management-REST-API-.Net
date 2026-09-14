namespace Library.Application.Books.Import;

/// <summary>
/// Reads rows out of an uploaded file, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IAsyncEnumerable{T}"/> is the whole point of this interface.</b>
/// The obvious signature — <c>Task&lt;List&lt;ImportedBookRow&gt;&gt;</c> — reads
/// the entire file into memory before the caller sees row one. A 200 MB supplier
/// feed then costs 200 MB of managed heap, most of it on the large object heap,
/// and the process is one concurrent upload away from failing.
/// </para>
/// <para>
/// Streaming keeps memory roughly constant regardless of file size: a row is
/// read, processed, and becomes garbage before the next is parsed. The caller
/// batches inserts itself, so the trade is bounded memory for slightly more
/// bookkeeping.
/// </para>
/// <para>
/// <b>Why the abstraction lives here.</b> <c>Library.Application</c> owns the
/// import use case but must not know that CSV parsing means CsvHelper, or that
/// JSON means <c>System.Text.Json</c>. Both readers live in
/// <c>Library.Infrastructure</c>; adding XML later adds one class and touches no
/// existing code.
/// </para>
/// <para>
/// <b>A <see cref="Stream"/>, not an <c>IFormFile</c>.</b> <c>IFormFile</c> is an
/// ASP.NET Core type, and a reference to it here would drag the web framework
/// into the Application layer for no gain. A stream is what the reader actually
/// needs, and it also makes these readers testable from a
/// <c>MemoryStream</c> with no HTTP involved.
/// </para>
/// </remarks>
public interface IBookImportReader
{
    /// <summary>The format this reader handles.</summary>
    ImportFormat Format { get; }

    /// <summary>
    /// Streams rows from <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row that cannot be read yields an <see cref="ImportRow{T}"/> carrying a
    /// <see cref="ImportRow{T}.ReadError"/> rather than throwing. One malformed
    /// line must not abandon the rows after it — that is the difference between
    /// "four rows need fixing" and "the import failed".
    /// </para>
    /// <para>
    /// A failure that genuinely prevents reading at all — a file that is not
    /// CSV, JSON that is not an array — does throw, because there is no row to
    /// attribute it to.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<ImportRow<ImportedBookRow>> ReadAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}

/// <summary>Selects the reader for a format.</summary>
/// <remarks>
/// A factory rather than a <c>switch</c> in the service, so that the service
/// depends on the abstraction and the set of supported formats is a DI
/// registration detail. Open/Closed at the level that matters here: a new format
/// is a new class plus one registration.
/// </remarks>
public interface IBookImportReaderFactory
{
    /// <summary>Returns the reader for <paramref name="format"/>.</summary>
    /// <exception cref="Domain.Exceptions.BusinessRuleViolationException">
    /// No reader is registered for that format.
    /// </exception>
    IBookImportReader GetReader(ImportFormat format);
}
