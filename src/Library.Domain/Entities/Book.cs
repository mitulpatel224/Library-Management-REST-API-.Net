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
        Isbn = isbn;
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
    /// The persisted ISBN column: 13 plain digits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a backing field instead of mapping <see cref="Isbn"/> through an EF
    /// value converter.</b> A converter makes the whole <c>Isbn</c> object the
    /// mapped property, and EF then applies that conversion to <i>both</i> sides
    /// of any comparison. Equality survives that - the constant converts
    /// cleanly - but a <c>LIKE</c> does not: EF tries to convert the pattern
    /// <c>"%design%"</c> into an <c>Isbn</c> and throws
    /// <c>InvalidCastException</c> at parameter binding.
    /// </para>
    /// <para>
    /// Storing a plain string in this field and wrapping it in the property below
    /// gives both halves: the database sees an ordinary indexable
    /// <c>TEXT</c>/<c>NVARCHAR</c> column that <c>LIKE</c> and range queries work
    /// on normally, while the domain still hands out a validated
    /// <see cref="ValueObjects.Isbn"/> that cannot hold a malformed value.
    /// </para>
    /// <para>
    /// Queries address this field by name - <c>EF.Property&lt;string&gt;(b, "_isbn")</c> -
    /// which is what <see cref="IsbnPropertyName"/> exists to keep in one place.
    /// </para>
    /// </remarks>
    private string _isbn = null!;

    /// <summary>The EF property name of the ISBN column, for use in queries.</summary>
    public const string IsbnPropertyName = "_isbn";

    /// <summary>Validated ISBN-13. Unique across the catalogue.</summary>
    public Isbn Isbn
    {
        get => ValueObjects.Isbn.FromTrustedValue(_isbn);
        private set => _isbn = value.Value;
    }

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
