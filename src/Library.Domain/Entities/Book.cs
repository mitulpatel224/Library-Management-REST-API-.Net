using Library.Domain.Common;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.Domain.Entities;

/// <summary>
/// A bibliographic title - the work, not a physical object.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Book / BookCopy split is the central modelling decision here.</b>
/// A <see cref="Book"/> is the catalogue record: one row per ISBN, carrying
/// title, authors, publisher, and genres. A <see cref="BookCopy"/> is a physical
/// item on a shelf, with its own barcode and condition. A library that owns
/// three copies of the same title has one Book row and three BookCopy rows.
/// </para>
/// <para>
/// Loans point at a <see cref="BookCopy"/>, never at a Book. That is what makes
/// "this copy is out, that one is available" expressible at all - and it is why
/// availability is a count over copies rather than a boolean on the title.
/// </para>
/// <para>
/// <b>Aggregate boundary.</b> Book is the aggregate root for its copies, authors,
/// and genres: those collections are exposed read-only and mutated only through
/// the methods below, so the invariants live in one place. Category, Publisher,
/// Author, and Genre are separate aggregates referenced by id - a book holds an
/// author's id, not responsibility for that author's data.
/// </para>
/// </remarks>
public sealed class Book : AuditableEntity
{
    private Book() { }

    private Book(
        Isbn isbn,
        string title,
        string? subtitle,
        int categoryId,
        int? publisherId,
        DateOnly? publishedOn,
        string? language,
        int? pageCount,
        string? description)
    {
        Isbn = isbn.Value;
        Title = title;
        Subtitle = subtitle;
        CategoryId = categoryId;
        PublisherId = publisherId;
        PublishedOn = publishedOn;
        Language = language;
        PageCount = pageCount;
        Description = description;
    }

    /// <summary>
    /// The ISBN-13 as stored: 13 digits, no hyphens. Unique across the catalogue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A plain string, deliberately — not a mapped <see cref="ValueObjects.Isbn"/>.</b>
    /// This is the pattern the whole solution follows for value objects, and it
    /// was arrived at by getting it wrong twice.
    /// </para>
    /// <para>
    /// Mapping the value object through an EF value converter looks correct and
    /// breaks every SQL operation except equality. EF applies the conversion to
    /// <i>both</i> sides of a comparison, so <c>LIKE '%design%'</c> tries to
    /// convert the pattern into an <c>Isbn</c> and throws at parameter binding.
    /// Hiding the column behind a private field fixes <c>LIKE</c> but leaves the
    /// property unmapped, so <c>ORDER BY</c> on it throws too — a bug that
    /// shipped here and went unnoticed because nothing exercised
    /// <c>?sortBy=isbn</c>.
    /// </para>
    /// <para>
    /// So: <b>the entity stores the primitive, and the value object guards the
    /// boundary.</b> <see cref="Create"/> takes an <see cref="ValueObjects.Isbn"/>,
    /// which cannot be constructed from a malformed input — a book with an
    /// invalid ISBN is still unrepresentable. What changes is that persistence
    /// sees an ordinary indexable column, so filtering, searching and sorting
    /// all behave like any other string.
    /// </para>
    /// </remarks>
    public string Isbn { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string? Subtitle { get; private set; }

    public int CategoryId { get; private set; }

    public Category Category { get; private set; } = null!;

    public int? PublisherId { get; private set; }

    public Publisher? Publisher { get; private set; }

    public DateOnly? PublishedOn { get; private set; }

    /// <summary>ISO 639-1 language code, e.g. "en".</summary>
    public string? Language { get; private set; }

    public int? PageCount { get; private set; }

    public string? Description { get; private set; }

    private readonly List<BookAuthor> _bookAuthors = [];
    public IReadOnlyCollection<BookAuthor> BookAuthors => _bookAuthors.AsReadOnly();

    private readonly List<BookGenre> _bookGenres = [];
    public IReadOnlyCollection<BookGenre> BookGenres => _bookGenres.AsReadOnly();

    private readonly List<BookCopy> _copies = [];
    public IReadOnlyCollection<BookCopy> Copies => _copies.AsReadOnly();

    /// <summary>Total physical copies held, in any state.</summary>
    public int TotalCopies => _copies.Count;

    /// <summary>
    /// Copies currently on the shelf.
    /// </summary>
    /// <remarks>
    /// Computed from the loaded collection, so it is only meaningful when copies
    /// have been eagerly loaded. Listing queries project this in SQL instead -
    /// see the catalogue read model - precisely to avoid loading every copy of
    /// every book just to display a count.
    /// </remarks>
    public int AvailableCopies => _copies.Count(c => c.Status == Enums.CopyStatus.Available);

    public static Book Create(
        Isbn isbn,
        string title,
        int categoryId,
        string? subtitle = null,
        int? publisherId = null,
        DateOnly? publishedOn = null,
        string? language = null,
        int? pageCount = null,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new BusinessRuleViolationException("book.title_required", "Book title is required.");
        }

        if (categoryId <= 0)
        {
            throw new BusinessRuleViolationException(
                "book.category_required", "A book must belong to a category.");
        }

        if (pageCount is <= 0)
        {
            throw new BusinessRuleViolationException(
                "book.invalid_page_count", "Page count must be greater than zero.");
        }

        return new Book(
            isbn,
            title.Trim(),
            subtitle?.Trim(),
            categoryId,
            publisherId,
            publishedOn,
            language?.Trim(),
            pageCount,
            description?.Trim());
    }

    public void UpdateDetails(
        string title,
        string? subtitle,
        int categoryId,
        int? publisherId,
        DateOnly? publishedOn,
        string? language,
        int? pageCount,
        string? description)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new BusinessRuleViolationException("book.title_required", "Book title is required.");
        }

        if (categoryId <= 0)
        {
            throw new BusinessRuleViolationException(
                "book.category_required", "A book must belong to a category.");
        }

        if (pageCount is <= 0)
        {
            throw new BusinessRuleViolationException(
                "book.invalid_page_count", "Page count must be greater than zero.");
        }

        Title = title.Trim();
        Subtitle = subtitle?.Trim();
        CategoryId = categoryId;
        PublisherId = publisherId;
        PublishedOn = publishedOn;
        Language = language?.Trim();
        PageCount = pageCount;
        Description = description?.Trim();
    }

    /// <summary>
    /// Replaces the author list, renumbering credit order from the given sequence.
    /// </summary>
    /// <remarks>
    /// Replace-rather-than-merge keeps the operation idempotent: PUT with the same
    /// list twice produces the same result, and the caller never has to compute a
    /// diff. The cost is that EF Core deletes and re-inserts join rows, which is
    /// acceptable because the list is small and edited rarely.
    /// </remarks>
    public void SetAuthors(IEnumerable<int> authorIdsInOrder)
    {
        ArgumentNullException.ThrowIfNull(authorIdsInOrder);

        List<int> ids = [.. authorIdsInOrder];

        if (ids.Distinct().Count() != ids.Count)
        {
            throw new BusinessRuleViolationException(
                "book.duplicate_author", "The same author cannot be listed twice on one book.");
        }

        _bookAuthors.Clear();

        for (int i = 0; i < ids.Count; i++)
        {
            _bookAuthors.Add(BookAuthor.Create(Id, ids[i], authorOrder: i + 1));
        }
    }

    /// <summary>Replaces the genre list.</summary>
    public void SetGenres(IEnumerable<int> genreIds)
    {
        ArgumentNullException.ThrowIfNull(genreIds);

        List<int> ids = [.. genreIds.Distinct()];

        _bookGenres.Clear();

        foreach (int genreId in ids)
        {
            _bookGenres.Add(BookGenre.Create(Id, genreId));
        }
    }

    /// <summary>Adds a physical copy, rejecting a barcode already used on this title.</summary>
    public BookCopy AddCopy(
        string barcode,
        Enums.CopyCondition condition = Enums.CopyCondition.New,
        string? shelfLocation = null,
        DateOnly? acquiredOn = null)
    {
        if (_copies.Any(c => string.Equals(c.Barcode, barcode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConflictException(
                "copy.duplicate_barcode",
                $"A copy with barcode '{barcode}' already exists for this book.");
        }

        BookCopy copy = BookCopy.Create(Id, barcode, condition, shelfLocation, acquiredOn);
        _copies.Add(copy);

        return copy;
    }
}
