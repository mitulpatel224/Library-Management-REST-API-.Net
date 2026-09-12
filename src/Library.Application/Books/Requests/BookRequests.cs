using Library.Domain.Enums;

namespace Library.Application.Books.Requests;

/// <summary>
/// Payload for creating a book.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dedicated request type, never the entity.</b> Binding straight to
/// <c>Book</c> would let a caller set <c>Id</c>, <c>CreatedAt</c>, or any other
/// property the model happens to expose — the mass-assignment vulnerability
/// (OWASP API6). This type lists exactly what a client is allowed to supply, so
/// anything else is silently ignored rather than trusted.
/// </para>
/// <para>
/// It is also why <c>Book</c> keeps `private set` on every property: even if
/// this DTO were bypassed, there is no public setter to assign.
/// </para>
/// </remarks>
public sealed record CreateBookRequest
{
    /// <summary>ISBN-13. Hyphens are accepted and stripped.</summary>
    public string Isbn { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Subtitle { get; init; }

    public int CategoryId { get; init; }

    public int? PublisherId { get; init; }

    public DateOnly? PublishedOn { get; init; }

    /// <summary>ISO 639-1 code, e.g. "en".</summary>
    public string? Language { get; init; }

    public int? PageCount { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Author ids, in credit order. Position in this list becomes
    /// <c>BookAuthor.AuthorOrder</c>.
    /// </summary>
    public IReadOnlyList<int> AuthorIds { get; init; } = [];

    public IReadOnlyList<int> GenreIds { get; init; } = [];
}

/// <summary>
/// Payload for replacing a book's details.
/// </summary>
/// <remarks>
/// <b>ISBN is deliberately absent.</b> It is the natural key — one ISBN is one
/// title — so changing it would turn this book into a different book. If a book
/// was catalogued under the wrong ISBN, the correct fix is to delete it and add
/// the right one, not to mutate identity in place.
/// </remarks>
public sealed record UpdateBookRequest
{
    public string Title { get; init; } = string.Empty;

    public string? Subtitle { get; init; }

    public int CategoryId { get; init; }

    public int? PublisherId { get; init; }

    public DateOnly? PublishedOn { get; init; }

    public string? Language { get; init; }

    public int? PageCount { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Replaces the author list entirely.
    /// </summary>
    /// <remarks>
    /// Replace rather than merge keeps <c>PUT</c> idempotent: sending the same
    /// body twice produces the same result, and the caller never has to compute
    /// a diff. That is what <c>PUT</c> means — a full representation of the
    /// resource, not a patch.
    /// </remarks>
    public IReadOnlyList<int> AuthorIds { get; init; } = [];

    public IReadOnlyList<int> GenreIds { get; init; } = [];
}

/// <summary>Payload for adding a physical copy to a book.</summary>
public sealed record AddBookCopyRequest
{
    /// <summary>Scannable barcode. Must be unique across the whole library.</summary>
    public string Barcode { get; init; } = string.Empty;

    public CopyCondition Condition { get; init; } = CopyCondition.New;

    /// <summary>Shelf reference, e.g. "A-12-3".</summary>
    public string? ShelfLocation { get; init; }

    public DateOnly? AcquiredOn { get; init; }
}

/// <summary>Payload for updating a physical copy.</summary>
/// <remarks>
/// <para>
/// <b>Barcode is editable, so a damaged or unreadable label can be re-issued.</b>
/// The alternative — delete and recreate — would discard the copy's loan history,
/// which is the wrong trade for a torn sticker.
/// </para>
/// <para>
/// It carries the same format and library-wide uniqueness rules as creation.
/// Changing it here is a real re-labelling: whoever does it is expected to put
/// the new label on the physical item, or the database and the shelf disagree.
/// </para>
/// <para>
/// <b>Status is still absent, deliberately.</b> A copy becomes <c>OnLoan</c> by
/// being issued and <c>Available</c> by being returned. Letting a client set it
/// directly would allow a copy to be marked available while a member still holds
/// it, desynchronising it from the loan rows that are the source of truth.
/// </para>
/// </remarks>
public sealed record UpdateBookCopyRequest
{
    /// <summary>Scannable barcode. Must be unique across the whole library.</summary>
    public string Barcode { get; init; } = string.Empty;

    public CopyCondition Condition { get; init; }

    /// <summary>Shelf reference, e.g. "A-12-3".</summary>
    public string? ShelfLocation { get; init; }
}
