using Library.Application.Common.Models;

namespace Library.Application.Books;

/// <summary>
/// Every filter, sort, and paging option the catalogue listing accepts.
/// </summary>
/// <remarks>
/// One request object rather than fourteen controller parameters. That keeps the
/// action signature readable, lets model binding populate it from the query
/// string automatically, and gives validation a single thing to inspect.
/// </remarks>
public sealed record BookSearchRequest : PageRequest
{
    /// <summary>Free-text match across title, subtitle, and ISBN.</summary>
    public string? Search { get; init; }

    /// <summary>Exact ISBN lookup. Hyphens are ignored.</summary>
    public string? Isbn { get; init; }

    /// <summary>Partial match on author first or last name.</summary>
    public string? Author { get; init; }

    public int? CategoryId { get; init; }

    public int? GenreId { get; init; }

    public int? PublisherId { get; init; }

    /// <summary>Lower bound on publication date, inclusive.</summary>
    public DateOnly? PublishedFrom { get; init; }

    /// <summary>Upper bound on publication date, inclusive.</summary>
    public DateOnly? PublishedTo { get; init; }

    public string? Language { get; init; }

    /// <summary>When true, returns only titles with at least one copy on the shelf.</summary>
    public bool AvailableOnly { get; init; }

    /// <summary>
    /// Sort field. Must be one of the keys in <see cref="BookSortOptions.SortMap"/>.
    /// </summary>
    public string? SortBy { get; init; }

    /// <summary><c>asc</c> or <c>desc</c>. Defaults to ascending.</summary>
    public string? SortDir { get; init; }

    /// <summary>True when <see cref="SortDir"/> asks for descending order.</summary>
    public bool IsDescending =>
        string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase);
}
