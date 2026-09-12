namespace Library.Application.Common.Models;

/// <summary>
/// Paging parameters accepted by every collection endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MaxPageSize"/> is a security control, not a convenience. Without a
/// server-side ceiling, <c>?pageSize=1000000</c> is a one-request denial of
/// service: the database materialises a million rows, the serializer allocates
/// them all, and the process dies. The value is clamped silently rather than
/// rejected, because a client asking for too much should still get a useful
/// answer.
/// </para>
/// <para>
/// The properties are settable so ASP.NET Core model binding can populate them
/// from the query string; normalisation happens in <see cref="Normalize"/>,
/// which every handler calls before touching the database.
/// </para>
/// </remarks>
public record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>1-based page number. Values below 1 are clamped to 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Items per page. Clamped to [1, <see cref="MaxPageSize"/>].</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Number of rows to skip. Derived - never sent by the client.</summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>Returns a copy with <see cref="Page"/> and <see cref="PageSize"/> forced into range.</summary>
    public PageRequest Normalize() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => PageSize,
        },
    };
}
