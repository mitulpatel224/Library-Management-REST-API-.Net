using System.Linq.Expressions;
using Library.Domain.Entities;

namespace Library.Application.Books;

/// <summary>
/// The allowed sort fields for the catalogue listing, and the expression each maps to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because of a specific vulnerability.</b> The obvious way
/// to implement dynamic sorting is to interpolate the caller's string into the
/// query:
/// </para>
/// <code>
/// // NEVER do this:
/// query.OrderBy($"{request.SortBy} {request.SortDir}");          // dynamic LINQ
/// context.Books.FromSqlRaw($"SELECT * FROM Books ORDER BY {sortBy}");  // worse
/// </code>
/// <para>
/// Both hand the caller control of the SQL. Parameterisation cannot save you
/// here - a parameter can only stand in for a <i>value</i>, never for an
/// identifier or a keyword, so <c>ORDER BY @p</c> is not valid SQL and there is
/// no escaping trick that makes it safe.
/// </para>
/// <para>
/// <b>The whitelist is the fix.</b> The caller's string is never used to build a
/// query; it is used to <i>look one up</i>. An unrecognised value matches
/// nothing and falls back to the default sort. That turns an open-ended
/// injection surface into a closed set of five choices - and it is the SOLID
/// Open/Closed principle doing real work: adding a sort field means adding a
/// dictionary entry, not touching the query code.
/// </para>
/// <para>
/// The comparison is deliberately case-insensitive, so <c>?sortBy=Title</c> and
/// <c>?sortBy=title</c> both work - a usability nicety that costs no safety,
/// because the lookup is still exact.
/// </para>
/// </remarks>
public static class BookSortOptions
{
    /// <summary>Used when the caller supplies no sort, or an unrecognised one.</summary>
    public const string Default = "title";

    /// <summary>
    /// Allowed sort keys mapped to the expression EF Core translates to
    /// <c>ORDER BY</c>.
    /// </summary>
    /// <remarks>
    /// The values are <see cref="Expression"/> trees, not compiled delegates.
    /// That distinction matters: an <c>Expression&lt;Func&lt;...&gt;&gt;</c> can be
    /// inspected and translated into SQL, so the database does the sorting. A
    /// compiled <c>Func&lt;...&gt;</c> would force EF Core to pull every matching
    /// row into memory and sort there - correct results, ruinous performance.
    /// </remarks>
    public static IReadOnlyDictionary<string, Expression<Func<Book, object?>>> SortMap { get; } =
        new Dictionary<string, Expression<Func<Book, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = book => book.Title,
            ["isbn"] = book => book.Isbn,
            ["published"] = book => book.PublishedOn,
            ["category"] = book => book.Category.Name,
            ["created"] = book => book.CreatedAt,
        };

    /// <summary>The sort keys a client may send. Surfaced in the API docs.</summary>
    public static IReadOnlyCollection<string> AllowedFields { get; } = [.. SortMap.Keys];

    /// <summary>
    /// Resolves a caller-supplied sort key, falling back to the default when it
    /// is absent or not on the whitelist.
    /// </summary>
    /// <remarks>
    /// Falling back rather than rejecting is a deliberate choice: an unknown sort
    /// field is a client bug, not an attack worth a 400, and returning a sensibly
    /// ordered page is more useful than an error. The security property is
    /// unaffected either way - the value never reaches the query.
    /// </remarks>
    public static Expression<Func<Book, object?>> Resolve(string? sortBy)
    {
        if (!string.IsNullOrWhiteSpace(sortBy) &&
            SortMap.TryGetValue(sortBy.Trim(), out Expression<Func<Book, object?>>? expression))
        {
            return expression;
        }

        return SortMap[Default];
    }

    /// <summary>True when the key is on the whitelist. Used by validation to warn the caller.</summary>
    public static bool IsAllowed(string? sortBy) =>
        string.IsNullOrWhiteSpace(sortBy) || SortMap.ContainsKey(sortBy.Trim());
}
