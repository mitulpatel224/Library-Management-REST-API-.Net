namespace Library.Application.Books.Import;

/// <summary>The file formats the importer accepts.</summary>
public enum ImportFormat
{
    Csv = 0,
    Json = 1,
}

/// <summary>
/// What to do when an incoming row carries an ISBN the catalogue already holds.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>Strategy</b> pattern, and the reason it is a caller choice
/// rather than a fixed rule is that all three are correct in different
/// situations:
/// </para>
/// <list type="bullet">
///   <item><see cref="Skip"/> — a monthly supplier feed that resends the whole
///     catalogue. Most rows are already known and re-importing them would churn
///     <c>UpdatedAt</c> on thousands of untouched books.</item>
///   <item><see cref="Update"/> — a corrected feed, where the incoming data is
///     newer than what is held.</item>
///   <item><see cref="Fail"/> — a one-off load into what should be an empty
///     catalogue. A duplicate means the file is wrong, and continuing would hide
///     that.</item>
/// </list>
/// </remarks>
public enum DuplicateHandling
{
    /// <summary>Leave the existing book untouched and report the row as skipped.</summary>
    Skip = 0,

    /// <summary>Overwrite the existing book's details from the incoming row.</summary>
    Update = 1,

    /// <summary>Reject the row. The rest of the file still imports.</summary>
    Fail = 2,
}

/// <summary>
/// One row as it arrived, before any domain meaning is attached.
/// </summary>
/// <remarks>
/// <para>
/// Every property is a nullable string, deliberately. This type models <b>what
/// the file said</b>, not what a book is — and a file says whatever someone
/// typed into it. Parsing <c>pageCount</c> into an <c>int</c> here would mean the
/// reader throws on row 4,000 of a 10,000-row upload and the caller learns
/// nothing about rows 1 to 3,999.
/// </para>
/// <para>
/// Conversion happens per row, inside a guard, so a bad value costs one row and
/// produces a message naming the line and the field.
/// </para>
/// </remarks>
public sealed record ImportedBookRow
{
    public string? Isbn { get; init; }

    public string? Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>
    /// Category <b>name</b>, not id.
    /// </summary>
    /// <remarks>
    /// A supplier's file says "Science Fiction"; it has no idea what
    /// <c>categoryId = 4</c> means in this database. Resolving names to ids — and
    /// creating the lookup rows that are missing — is the importer's job.
    /// </remarks>
    public string? Category { get; init; }

    public string? Publisher { get; init; }

    public string? PublishedOn { get; init; }

    public string? Language { get; init; }

    public string? PageCount { get; init; }

    public string? Description { get; init; }

    /// <summary>Author names, separated by <c>;</c>. Order becomes credit order.</summary>
    public string? Authors { get; init; }

    /// <summary>Genre names, separated by <c>;</c>.</summary>
    public string? Genres { get; init; }
}

/// <summary>
/// One row, paired with its position in the file and any error from reading it.
/// </summary>
/// <remarks>
/// The line number is carried from the reader onward because it is the only
/// thing that makes an error actionable. "ISBN check digit is invalid" is
/// useless against a 10,000-row file; "line 4,213: ISBN check digit is invalid"
/// is a fix.
/// </remarks>
/// <typeparam name="T">The parsed row type.</typeparam>
public sealed record ImportRow<T>
{
    /// <summary>1-based line number in the source file, including any header.</summary>
    public int LineNumber { get; init; }

    public T? Value { get; init; }

    /// <summary>Set when the row could not be read at all — malformed CSV, bad JSON.</summary>
    public string? ReadError { get; init; }

    public bool IsReadable => ReadError is null && Value is not null;
}

/// <summary>Why a single row was not imported.</summary>
public sealed record ImportRowError
{
    public int LineNumber { get; init; }

    /// <summary>The ISBN as it appeared, so the row is identifiable in the source file.</summary>
    public string? Isbn { get; init; }

    /// <summary>Stable machine-readable code, matching the API's error-code convention.</summary>
    public string ErrorCode { get; init; } = null!;

    public string Message { get; init; } = null!;
}

/// <summary>
/// The outcome of an import.
/// </summary>
/// <remarks>
/// <para>
/// <b>Partial success is the normal case</b>, which is why this reports counts
/// and per-row errors rather than throwing. A 10,000-row file with four bad rows
/// should import 9,996 books and tell the librarian which four to fix — not
/// reject the lot.
/// </para>
/// <para>
/// The endpoint returns <c>200</c> even when some rows failed. The request
/// itself succeeded; the failures are data, reported in the body. A <c>4xx</c>
/// would say the upload was wrong, which it was not.
/// </para>
/// </remarks>
public sealed record BookImportResult
{
    /// <summary>Rows read from the file, excluding the header.</summary>
    public int TotalRows { get; init; }

    /// <summary>Books newly catalogued.</summary>
    public int Imported { get; init; }

    /// <summary>Existing books overwritten, under <see cref="DuplicateHandling.Update"/>.</summary>
    public int Updated { get; init; }

    /// <summary>Rows whose ISBN was already held, under <see cref="DuplicateHandling.Skip"/>.</summary>
    public int Skipped { get; init; }

    /// <summary>Rows rejected. Each has an entry in <see cref="Errors"/>.</summary>
    public int Failed { get; init; }

    /// <summary>Lookup rows created on the fly — categories, publishers, authors, genres.</summary>
    public int LookupsCreated { get; init; }

    public IReadOnlyList<ImportRowError> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}
