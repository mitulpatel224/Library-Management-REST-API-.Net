using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Library.Application.Books.Import;
using Library.Domain.Exceptions;

namespace Library.Infrastructure.Import;

/// <summary>
/// Streams book rows out of a JSON array.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>JsonSerializer.DeserializeAsyncEnumerable</c>, which parses the array
/// incrementally. <c>DeserializeAsync&lt;List&lt;T&gt;&gt;</c> would hold every
/// element in memory at once — the failure mode the CSV reader also avoids.
/// </para>
/// <para>
/// <b>"Line number" means array index here.</b> JSON has no meaningful lines —
/// the file may be minified onto one. The index is what lets someone find the
/// offending element, so it is reported in the same field and the API documents
/// what it means per format.
/// </para>
/// <para>
/// <b>A malformed JSON file imports nothing, unlike a malformed CSV.</b> That is
/// a genuine behavioural difference and worth knowing before uploading a large
/// file. CSV is line-oriented, so a broken row is reported and the reader
/// continues — 9,996 of 10,000 rows still import. The JSON reader buffers ahead,
/// so a syntax error anywhere surfaces before the elements preceding it have
/// been yielded; perfectly valid earlier elements never arrive. The caller has
/// to fix the syntax and re-upload.
/// </para>
/// </remarks>
public sealed class JsonBookImportReader : IBookImportReader
{
    public ImportFormat Format => ImportFormat.Json;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Lenient on purpose, and the exact opposite of the API's strict binding.
        //
        // The API rejects unknown properties, because a caller sending a field we
        // ignore has misunderstood a contract we own. An import file comes from a
        // supplier with their own schema: extra fields are expected, and refusing
        // the file over one we do not need would make the feature unusable.
        //
        // Strict where we own both ends; lenient where we own neither.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async IAsyncEnumerable<ImportRow<ImportedBookRow>> ReadAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        IAsyncEnumerator<ImportedBookRow?> enumerator =
            JsonSerializer.DeserializeAsyncEnumerable<ImportedBookRow>(
                    source, Options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

        int index = 0;

        try
        {
            while (true)
            {
                bool moved;
                string? parseError = null;

                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    // Unlike CSV, a JSON syntax error desynchronises the reader
                    // irrecoverably - the parser cannot know where the next element
                    // begins. Report where it failed and stop.
                    moved = false;
                    parseError = $"The JSON could not be parsed at element {index + 1}: {ex.Message}";
                }

                if (parseError is not null)
                {
                    index++;
                    yield return new ImportRow<ImportedBookRow>
                    {
                        LineNumber = index,
                        ReadError = parseError,
                    };

                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                index++;

                yield return enumerator.Current is null
                    ? new ImportRow<ImportedBookRow>
                    {
                        LineNumber = index,
                        ReadError = "The array element was null.",
                    }
                    : new ImportRow<ImportedBookRow>
                    {
                        LineNumber = index,
                        Value = enumerator.Current,
                    };
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Selects the reader registered for a format.</summary>
public sealed class BookImportReaderFactory : IBookImportReaderFactory
{
    private readonly Dictionary<ImportFormat, IBookImportReader> _readers;

    /// <remarks>
    /// Takes every registered <see cref="IBookImportReader"/> and indexes them by
    /// the format each declares. Adding XML support is a new class plus one DI
    /// registration — this class does not change, which is Open/Closed doing
    /// something real rather than being cited.
    /// </remarks>
    public BookImportReaderFactory(IEnumerable<IBookImportReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);

        _readers = readers.ToDictionary(r => r.Format);
    }

    public IBookImportReader GetReader(ImportFormat format)
    {
        return _readers.TryGetValue(format, out IBookImportReader? reader)
            ? reader
            : throw new BusinessRuleViolationException(
                "import.unsupported_format",
                $"No reader is registered for the '{format}' format.");
    }
}
