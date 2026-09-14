using System.Globalization;
using Library.Application.Common.Abstractions;
using Library.Domain.Entities;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.Application.Books.Import;

/// <summary>Bulk import of books from an uploaded file.</summary>
public interface IBookImportService
{
    /// <summary>
    /// Imports books from <paramref name="source"/>, reporting per-row outcomes.
    /// </summary>
    /// <remarks>
    /// Does not throw for bad rows — they are counted and described in the
    /// result. It throws only when the file cannot be read at all.
    /// </remarks>
    Task<BookImportResult> ImportAsync(
        Stream source,
        ImportFormat format,
        DuplicateHandling duplicateHandling,
        CancellationToken cancellationToken = default);

    /// <summary>Produces a CSV template with the expected header and one example row.</summary>
    string BuildCsvTemplate();
}

/// <inheritdoc cref="IBookImportService"/>
public sealed class BookImportService : IBookImportService
{
    /// <summary>
    /// Rows buffered before each <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving once per row is one round trip per book — unusably slow on a large
    /// file. Saving once at the very end means EF Core's change tracker holds
    /// every entity for the whole import, and its per-save fixup work grows with
    /// the number tracked, so the last rows run far slower than the first.
    /// </para>
    /// <para>
    /// Batching bounds both. 100 is a pragmatic middle: large enough that the
    /// round trips disappear, small enough that the tracker stays cheap.
    /// </para>
    /// </remarks>
    private const int BatchSize = 100;

    /// <summary>Separator for the author and genre columns.</summary>
    /// <remarks>
    /// A semicolon rather than a comma, because the file is CSV and a comma
    /// inside a field forces quoting that half the tools producing these files
    /// get wrong.
    /// </remarks>
    private const char ListSeparator = ';';

    private readonly IBookImportReaderFactory _readerFactory;
    private readonly IBookRepository _repository;
    private readonly ILookupResolver _lookups;
    private readonly IUnitOfWork _unitOfWork;

    public BookImportService(
        IBookImportReaderFactory readerFactory,
        IBookRepository repository,
        ILookupResolver lookups,
        IUnitOfWork unitOfWork)
    {
        _readerFactory = readerFactory;
        _repository = repository;
        _lookups = lookups;
        _unitOfWork = unitOfWork;
    }

    public async Task<BookImportResult> ImportAsync(
        Stream source,
        ImportFormat format,
        DuplicateHandling duplicateHandling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        IBookImportReader reader = _readerFactory.GetReader(format);

        List<ImportRowError> errors = [];
        int total = 0, imported = 0, updated = 0, skipped = 0;
        int pending = 0;

        // ISBNs accepted earlier in THIS file. Without it, a file containing the
        // same ISBN twice passes the database check both times - the first is
        // still only pending in the change tracker - and the batch then fails at
        // the unique index, taking the whole batch with it.
        HashSet<string> seenInFile = new(StringComparer.Ordinal);

        // await foreach: rows arrive one at a time as the stream is read. The
        // file is never held in memory in full.
        await foreach (ImportRow<ImportedBookRow> row in
            reader.ReadAsync(source, cancellationToken).WithCancellation(cancellationToken))
        {
            total++;

            if (!row.IsReadable)
            {
                errors.Add(new ImportRowError
                {
                    LineNumber = row.LineNumber,
                    ErrorCode = "import.row_unreadable",
                    Message = row.ReadError ?? "The row could not be read.",
                });

                continue;
            }

            RowOutcome outcome = await ProcessRowAsync(
                row.LineNumber, row.Value!, duplicateHandling, seenInFile, cancellationToken);

            if (outcome.Error is not null)
            {
                errors.Add(outcome.Error);
                continue;
            }

            switch (outcome.Kind)
            {
                case OutcomeKind.Imported: imported++; pending++; break;
                case OutcomeKind.Updated: updated++; pending++; break;
                case OutcomeKind.Skipped: skipped++; break;
                default: break;
            }

            if (pending >= BatchSize)
            {
                await FlushAsync(cancellationToken);
                pending = 0;
            }
        }

        // Whatever is left over from the final, partial batch.
        if (pending > 0)
        {
            await FlushAsync(cancellationToken);
        }

        return new BookImportResult
        {
            TotalRows = total,
            Imported = imported,
            Updated = updated,
            Skipped = skipped,
            Failed = errors.Count,
            LookupsCreated = _lookups.CreatedCount,
            Errors = errors,
        };
    }

    /// <summary>
    /// Validates one row and stages the resulting insert or update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this catches <see cref="DomainException"/>.</b> Controllers in this
    /// solution never catch, because one bad request should become one error
    /// response. Here the unit of failure is a <i>row</i>, not the request: a
    /// malformed ISBN on line 4,213 must cost that line and nothing else.
    /// Letting it propagate would abandon the 6,000 rows after it.
    /// </para>
    /// <para>
    /// Note what is NOT caught: anything that is not a domain exception. A
    /// <c>DbUpdateException</c> or an <c>OperationCanceledException</c> means the
    /// import itself is in trouble, and swallowing it per row would turn a real
    /// failure into 10,000 confusing row errors.
    /// </para>
    /// </remarks>
    private async Task<RowOutcome> ProcessRowAsync(
        int lineNumber,
        ImportedBookRow raw,
        DuplicateHandling duplicateHandling,
        HashSet<string> seenInFile,
        CancellationToken cancellationToken)
    {
        try
        {
            // TryCreate, not Create: this is the non-throwing parse the value
            // object exposes precisely for bulk paths, so a bad ISBN produces a
            // message rather than an exception.
            if (!Isbn.TryCreate(raw.Isbn, out Isbn isbn, out string? isbnError))
            {
                return RowOutcome.Failure(lineNumber, raw.Isbn, "import.invalid_isbn", isbnError!);
            }

            if (!seenInFile.Add(isbn.Value))
            {
                return RowOutcome.Failure(
                    lineNumber, raw.Isbn, "import.duplicate_in_file",
                    $"ISBN {isbn.ToDisplayString()} appears more than once in this file.");
            }

            if (string.IsNullOrWhiteSpace(raw.Title))
            {
                return RowOutcome.Failure(
                    lineNumber, raw.Isbn, "import.title_required", "Title is required.");
            }

            if (string.IsNullOrWhiteSpace(raw.Category))
            {
                return RowOutcome.Failure(
                    lineNumber, raw.Isbn, "import.category_required", "Category is required.");
            }

            if (!TryParseOptionalDate(raw.PublishedOn, out DateOnly? publishedOn))
            {
                return RowOutcome.Failure(
                    lineNumber, raw.Isbn, "import.invalid_date",
                    $"'{raw.PublishedOn}' is not a valid date. Use yyyy-MM-dd.");
            }

            if (!TryParseOptionalInt(raw.PageCount, out int? pageCount))
            {
                return RowOutcome.Failure(
                    lineNumber, raw.Isbn, "import.invalid_page_count",
                    $"'{raw.PageCount}' is not a whole number.");
            }

            bool exists = await _repository.IsbnExistsAsync(isbn.Value, cancellationToken);

            if (exists)
            {
                switch (duplicateHandling)
                {
                    case DuplicateHandling.Skip:
                        return RowOutcome.Success(OutcomeKind.Skipped);

                    case DuplicateHandling.Fail:
                        return RowOutcome.Failure(
                            lineNumber, raw.Isbn, "import.duplicate_isbn",
                            $"ISBN {isbn.ToDisplayString()} is already catalogued.");

                    case DuplicateHandling.Update:
                        await UpdateExistingAsync(isbn, raw, publishedOn, pageCount, cancellationToken);
                        return RowOutcome.Success(OutcomeKind.Updated);

                    default:
                        return RowOutcome.Failure(
                            lineNumber, raw.Isbn, "import.unknown_strategy",
                            $"Unknown duplicate handling '{duplicateHandling}'.");
                }
            }

            await CreateNewAsync(isbn, raw, publishedOn, pageCount, cancellationToken);
            return RowOutcome.Success(OutcomeKind.Imported);
        }
        catch (DomainException ex)
        {
            // A rule the entity enforces that this method did not pre-check.
            // Caught so the row fails alone; the ErrorCode carries through
            // unchanged, so the caller sees the same code the API would return.
            return RowOutcome.Failure(lineNumber, raw.Isbn, ex.ErrorCode, ex.Message);
        }
    }

    private async Task CreateNewAsync(
        Isbn isbn,
        ImportedBookRow raw,
        DateOnly? publishedOn,
        int? pageCount,
        CancellationToken cancellationToken)
    {
        int categoryId = await _lookups.ResolveCategoryAsync(raw.Category!, cancellationToken);
        int? publisherId = await _lookups.ResolvePublisherAsync(raw.Publisher, cancellationToken);

        Book book = Book.Create(
            isbn,
            raw.Title!,
            categoryId,
            raw.Subtitle,
            publisherId,
            publishedOn,
            raw.Language,
            pageCount,
            raw.Description);

        _repository.Add(book);

        // Authors and genres are NOT set here. Their join rows carry the book's
        // key, which the database does not assign until the insert completes -
        // so they are applied after the batch saves. See ApplyRelationshipsAsync.
        _pendingRelationships.Add((book, Split(raw.Authors), Split(raw.Genres)));
    }

    private async Task UpdateExistingAsync(
        Isbn isbn,
        ImportedBookRow raw,
        DateOnly? publishedOn,
        int? pageCount,
        CancellationToken cancellationToken)
    {
        Book? existing = await _repository.GetEntityByIsbnAsync(isbn.Value, cancellationToken);

        if (existing is null)
        {
            // Raced with a delete between the existence check and here. Treat as
            // an insert rather than failing the row.
            await CreateNewAsync(isbn, raw, publishedOn, pageCount, cancellationToken);
            return;
        }

        int categoryId = await _lookups.ResolveCategoryAsync(raw.Category!, cancellationToken);
        int? publisherId = await _lookups.ResolvePublisherAsync(raw.Publisher, cancellationToken);

        existing.UpdateDetails(
            raw.Title!,
            raw.Subtitle,
            categoryId,
            publisherId,
            publishedOn,
            raw.Language,
            pageCount,
            raw.Description);

        // Already has a key, so the joins can be applied immediately.
        existing.SetAuthors(await _lookups.ResolveAuthorsAsync(Split(raw.Authors), cancellationToken));
        existing.SetGenres(await _lookups.ResolveGenresAsync(Split(raw.Genres), cancellationToken));
    }

    private readonly List<(Book Book, IReadOnlyList<string> Authors, IReadOnlyList<string> Genres)>
        _pendingRelationships = [];

    /// <summary>
    /// Commits the current batch, then attaches authors and genres to the books
    /// it just inserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two saves per batch, and the order is forced by the schema.</b>
    /// <c>BookAuthor</c> and <c>BookGenre</c> rows carry the book's key as half
    /// of their composite primary key, and the database does not assign that key
    /// until the book row is inserted. So the books go first, and only then can
    /// their join rows be written.
    /// </para>
    /// <para>
    /// This is the same constraint that makes member registration two saves —
    /// the membership number is derived from an id that does not exist yet.
    /// Whenever something depends on a database-generated key, it costs a second
    /// round trip.
    /// </para>
    /// <para>
    /// Updates do not appear here: an existing book already has its key, so
    /// <see cref="UpdateExistingAsync"/> sets its authors and genres immediately.
    /// </para>
    /// </remarks>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (_pendingRelationships.Count == 0)
        {
            return;
        }

        foreach ((Book book, IReadOnlyList<string> authors, IReadOnlyList<string> genres)
            in _pendingRelationships)
        {
            if (authors.Count > 0)
            {
                book.SetAuthors(await _lookups.ResolveAuthorsAsync(authors, cancellationToken));
            }

            if (genres.Count > 0)
            {
                book.SetGenres(await _lookups.ResolveGenresAsync(genres, cancellationToken));
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Cleared so the next batch does not re-apply this one's relationships -
        // harmless but wasteful, and it would grow unboundedly across a large file.
        _pendingRelationships.Clear();
    }

    /// <summary>
    /// Splits a delimited list, trimming and dropping blanks.
    /// </summary>
    /// <remarks>
    /// <c>"Gamma; Helm;; Johnson "</c> yields three names, not four. Files
    /// produced by hand routinely carry trailing separators and stray spaces,
    /// and a blank author name would otherwise become a lookup row.
    /// </remarks>
    private static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value
            .Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)];
    }

    /// <summary>
    /// Parses an optional date, treating blank as "not supplied" rather than invalid.
    /// </summary>
    /// <remarks>
    /// <c>InvariantCulture</c> and an explicit format, deliberately. Parsing
    /// <c>03/04/2024</c> with the server's culture makes it March in one
    /// deployment and April in another, and nothing in the data reveals which —
    /// a silent corruption that only surfaces in a date-range report months
    /// later. ISO 8601 is unambiguous.
    /// </remarks>
    private static bool TryParseOptionalDate(string? value, out DateOnly? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(
                value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseOptionalInt(string? value, out int? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    public string BuildCsvTemplate()
    {
        // The header must match what the CSV reader maps, so the template is
        // built from the same names rather than typed out twice.
        return
            "isbn,title,subtitle,category,publisher,publishedOn,language,pageCount,description,authors,genres\n" +
            "9780132350884,Clean Code,A Handbook of Agile Software Craftsmanship,Software Engineering," +
            "Prentice Hall,2008-08-01,en,464,Principles for writing readable code," +
            "Robert C. Martin,Programming;Software Design\n";
    }

    // ----------------------------------------------------------------- types

    private enum OutcomeKind
    {
        Imported,
        Updated,
        Skipped,
        Failed,
    }

    private sealed record RowOutcome
    {
        public OutcomeKind Kind { get; init; }

        public ImportRowError? Error { get; init; }

        public static RowOutcome Success(OutcomeKind kind) => new() { Kind = kind };

        public static RowOutcome Failure(
            int lineNumber, string? isbn, string errorCode, string message) =>
            new()
            {
                Kind = OutcomeKind.Failed,
                Error = new ImportRowError
                {
                    LineNumber = lineNumber,
                    Isbn = isbn,
                    ErrorCode = errorCode,
                    Message = message,
                },
            };
    }
}
