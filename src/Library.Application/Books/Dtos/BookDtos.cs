using Library.Domain.Enums;

namespace Library.Application.Books.Dtos;

/// <summary>
/// Lightweight projection for list and search results.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why DTOs at all, when the entity already has this data?</b> Three reasons,
/// and only the first is about tidiness:
/// </para>
/// <list type="number">
///   <item>
///     <b>The API contract stops being hostage to the schema.</b> Renaming a
///     column becomes a mapping change instead of a breaking change for every
///     client.
///   </item>
///   <item>
///     <b>Serialising an entity leaks whatever is attached to it.</b> Navigation
///     properties drag in related graphs, and cycles (Book -&gt; Copies -&gt; Book)
///     either throw or emit enormous payloads.
///   </item>
///   <item>
///     <b>Projecting to a DTO inside the query changes the SQL.</b> EF Core
///     translates a <c>Select</c> into the column list, so this type causes the
///     database to return exactly these fields - not <c>SELECT *</c> plus joins
///     for data nobody asked for.
///   </item>
/// </list>
/// <para>
/// A <c>record</c> rather than a class: DTOs are immutable data, and value
/// equality makes them trivial to assert on in tests.
/// </para>
/// </remarks>
public sealed record BookSummaryDto
{
    public int Id { get; init; }

    /// <summary>Unhyphenated 13-digit ISBN.</summary>
    public string Isbn { get; init; } = null!;

    public string Title { get; init; } = null!;

    public string? Subtitle { get; init; }

    public string CategoryName { get; init; } = null!;

    public string? PublisherName { get; init; }

    public DateOnly? PublishedOn { get; init; }

    /// <summary>Author display names, in credit order.</summary>
    public IReadOnlyList<string> Authors { get; init; } = [];

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Total physical copies held, in any state.</summary>
    public int TotalCopies { get; init; }

    /// <summary>Copies currently on the shelf.</summary>
    public int AvailableCopies { get; init; }

    /// <summary>True when at least one copy can be issued right now.</summary>
    public bool IsAvailable => AvailableCopies > 0;
}

/// <summary>Full detail for a single book, including its individual copies.</summary>
public sealed record BookDetailDto
{
    public int Id { get; init; }

    public string Isbn { get; init; } = null!;

    /// <summary>Hyphenated form for display, e.g. 978-0-13-235088-4.</summary>
    /// <remarks>
    /// Computed from <see cref="Isbn"/> rather than projected in the query.
    /// String slicing has no SQL translation, so putting this inside the EF Core
    /// projection would either fail to translate or silently drop the whole
    /// query to client-side evaluation.
    /// </remarks>
    public string IsbnDisplay => Isbn.Length == Domain.ValueObjects.Isbn.Length
        ? $"{Isbn[..3]}-{Isbn[3]}-{Isbn[4..6]}-{Isbn[6..12]}-{Isbn[12]}"
        : Isbn;

    public string Title { get; init; } = null!;

    public string? Subtitle { get; init; }

    public string? Description { get; init; }

    public int CategoryId { get; init; }

    public string CategoryName { get; init; } = null!;

    public int? PublisherId { get; init; }

    public string? PublisherName { get; init; }

    public DateOnly? PublishedOn { get; init; }

    public string? Language { get; init; }

    public int? PageCount { get; init; }

    public IReadOnlyList<AuthorSummaryDto> Authors { get; init; } = [];

    public IReadOnlyList<GenreSummaryDto> Genres { get; init; } = [];

    public IReadOnlyList<BookCopyDto> Copies { get; init; } = [];

    public int TotalCopies { get; init; }

    public int AvailableCopies { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record BookCopyDto
{
    public int Id { get; init; }

    public int BookId { get; init; }

    public string Barcode { get; init; } = null!;

    public CopyStatus Status { get; init; }

    public CopyCondition Condition { get; init; }

    public string? ShelfLocation { get; init; }

    public DateOnly? AcquiredOn { get; init; }

    public bool IsAvailable { get; init; }
}

public sealed record AuthorSummaryDto
{
    public int Id { get; init; }

    public string FullName { get; init; } = null!;

    /// <summary>1-based position in the credit list.</summary>
    public int AuthorOrder { get; init; }

    public string? Role { get; init; }
}

public sealed record GenreSummaryDto
{
    public int Id { get; init; }

    public string Name { get; init; } = null!;

    public string Slug { get; init; } = null!;
}

public sealed record CategorySummaryDto
{
    public int Id { get; init; }

    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    public int? ParentCategoryId { get; init; }

    public string? ParentCategoryName { get; init; }

    public int BookCount { get; init; }
}

public sealed record PublisherSummaryDto
{
    public int Id { get; init; }

    public string Name { get; init; } = null!;

    public string? Country { get; init; }

    public string? Website { get; init; }

    public int BookCount { get; init; }
}
