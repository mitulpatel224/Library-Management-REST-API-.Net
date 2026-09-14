using System.Globalization;
using System.Runtime.CompilerServices;
using CsvHelper;
using CsvHelper.Configuration;
using Library.Application.Books.Import;

namespace Library.Infrastructure.Import;

/// <summary>
/// Streams book rows out of a CSV file using CsvHelper.
/// </summary>
/// <remarks>
/// <para>
/// The reader never materialises the file. Rows are pulled one at a time as the
/// underlying stream is consumed, so a 200 MB upload costs a buffer rather than
/// 200 MB of managed heap.
/// </para>
/// <para>
/// <b>The error handling is the interesting part.</b> A row that fails to parse
/// becomes an <see cref="ImportRow{T}"/> carrying a
/// <see cref="ImportRow{T}.ReadError"/> and reading continues — one bad line
/// costs one line, not the 6,000 after it.
/// </para>
/// <para>
/// Note the shape of the loop: C# forbids <c>yield return</c> inside a
/// <c>catch</c> block, so each failure is captured into a local and yielded
/// after the <c>try</c> closes. That is why the code reads more awkwardly than
/// the intent suggests.
/// </para>
/// </remarks>
public sealed class CsvBookImportReader : IBookImportReader
{
    public ImportFormat Format => ImportFormat.Csv;

    public async IAsyncEnumerable<ImportRow<ImportedBookRow>> ReadAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Headers match case-insensitively and ignore underscores, so
            // "PageCount", "pagecount" and "page_count" all bind. These files come
            // from spreadsheets and other people's exports; rejecting correct data
            // over presentation helps nobody.
            PrepareHeaderForMatch = args =>
                args.Header.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Trim()
                    .ToLowerInvariant(),

            // A missing optional column must not abort the read. The service
            // decides what is required; the reader only parses.
            MissingFieldFound = null,
            HeaderValidated = null,

            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim,
            DetectColumnCountChanges = false,
        };

        // leaveOpen: the caller owns the stream. Disposing a request body here
        // would break anything that reads it afterwards.
        using var streamReader = new StreamReader(source, leaveOpen: true);
        using var csv = new CsvReader(streamReader, configuration);

        if (!await csv.ReadAsync().ConfigureAwait(false))
        {
            // No header: the file is empty. Not an error - an empty import
            // reports zero rows.
            yield break;
        }

        csv.ReadHeader();

        // Line 1 is the header, so data starts at 2. Tracked manually because
        // CsvHelper's own counter is unreliable once a row fails to parse.
        int lineNumber = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool hasRow;
            string? fatalError = null;

            try
            {
                hasRow = await csv.ReadAsync().ConfigureAwait(false);
            }
            catch (CsvHelperException ex)
            {
                // Structurally broken - an unterminated quote, typically, which
                // desynchronises the parser. Everything after it would be
                // nonsense, so report and stop.
                hasRow = false;
                fatalError = $"The file could not be parsed from this line onward: {ex.Message}";
            }

            if (fatalError is not null)
            {
                lineNumber++;
                yield return new ImportRow<ImportedBookRow>
                {
                    LineNumber = lineNumber,
                    ReadError = fatalError,
                };

                yield break;
            }

            if (!hasRow)
            {
                yield break;
            }

            lineNumber++;

            ImportedBookRow? value = null;
            string? rowError = null;

            try
            {
                value = new ImportedBookRow
                {
                    Isbn = GetField(csv, "isbn"),
                    Title = GetField(csv, "title"),
                    Subtitle = GetField(csv, "subtitle"),
                    Category = GetField(csv, "category"),
                    Publisher = GetField(csv, "publisher"),
                    PublishedOn = GetField(csv, "publishedon"),
                    Language = GetField(csv, "language"),
                    PageCount = GetField(csv, "pagecount"),
                    Description = GetField(csv, "description"),
                    Authors = GetField(csv, "authors"),
                    Genres = GetField(csv, "genres"),
                };
            }
            catch (CsvHelperException ex)
            {
                // This row alone is bad. Report it and keep reading.
                rowError = ex.Message;
            }

            yield return new ImportRow<ImportedBookRow>
            {
                LineNumber = lineNumber,
                Value = value,
                ReadError = rowError,
            };
        }
    }

    /// <summary>
    /// Reads a field by header name, returning null when the column is absent or blank.
    /// </summary>
    /// <remarks>
    /// Every optional column is genuinely optional: a file carrying only
    /// <c>isbn,title,category</c> imports cleanly. <c>TryGetField</c> rather than
    /// the indexer, because the indexer throws on a missing column and that would
    /// make every narrow file unreadable.
    /// </remarks>
    private static string? GetField(CsvReader csv, string name)
    {
        return csv.TryGetField(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }
}
