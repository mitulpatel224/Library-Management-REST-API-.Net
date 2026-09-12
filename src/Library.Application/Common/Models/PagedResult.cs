namespace Library.Application.Common.Models;

/// <summary>
/// One page of results plus the metadata a client needs to navigate the rest.
/// </summary>
/// <remarks>
/// <para>
/// Every collection endpoint in this API returns this shape. Returning a bare
/// <c>List&lt;T&gt;</c> is the single most common scaling mistake in a CRUD API:
/// it works perfectly against 20 seeded rows and falls over against 200,000.
/// </para>
/// <para>
/// <b>Offset paging</b> (<c>Skip</c>/<c>Take</c>) is used here. It is the right
/// call for a librarian's catalogue screen, which needs to jump to an arbitrary
/// page and show a total count. Its weakness is well known - <c>OFFSET 100000</c>
/// still makes the database walk 100,000 rows, and a row inserted mid-browse
/// shifts everything down a slot. Keyset (cursor) paging fixes both but cannot
/// jump to page 47 or report a total. Different tools; this screen wants this one.
/// </para>
/// </remarks>
/// <typeparam name="T">The item type, always a DTO - never a domain entity.</typeparam>
public sealed record PagedResult<T>
{
    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }

    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>1-based page number.</summary>
    public int Page { get; }

    /// <summary>Maximum items per page.</summary>
    public int PageSize { get; }

    /// <summary>Total matching items across all pages (before paging, after filtering).</summary>
    public int TotalCount { get; }

    /// <summary>Total number of pages available.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

/// <summary>
/// Factory helpers for <see cref="PagedResult{T}"/>.
/// </summary>
/// <remarks>
/// These live on a non-generic class rather than as static members of
/// <see cref="PagedResult{T}"/> because analyzer rule CA1000 forbids the latter:
/// a static member on a generic type must be called as
/// <c>PagedResult&lt;BookDto&gt;.Empty(...)</c>, forcing the caller to restate a
/// type argument the compiler could otherwise infer. Here,
/// <c>PagedResult.Empty&lt;BookDto&gt;(...)</c> reads the same but the generic
/// parameter belongs to the method, where it can be inferred from context.
/// </remarks>
public static class PagedResult
{
    /// <summary>An empty page, for short-circuiting a query that cannot match.</summary>
    public static PagedResult<T> Empty<T>(int page, int pageSize) =>
        new([], page, pageSize, 0);
}
