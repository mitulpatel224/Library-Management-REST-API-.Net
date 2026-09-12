using Library.Application.Books.Dtos;
using Library.Application.Common.Models;

namespace Library.Application.Books;

/// <summary>
/// Read access to the book catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface exists, given EF Core already provides one.</b>
/// <c>DbSet&lt;Book&gt;</c> is a repository and <c>DbContext</c> is a unit of work,
/// so wrapping them can be pure ceremony. The justification here is specific:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>It keeps <c>IQueryable</c> out of the layers above.</b> The return type
///     is <see cref="PagedResult{T}"/> - already materialised. A controller
///     cannot accidentally compose another <c>Where</c> onto a live query and
///     fire SQL from inside a view.
///   </item>
///   <item>
///     <b>It is the seam CQRS slides into.</b> When these become query handlers
///     in Phase 10, the implementation moves and the callers do not.
///   </item>
///   <item>
///     <b>It states the dependency direction.</b> This interface lives in
///     Application; its implementation lives in Infrastructure. Application
///     compiles with no knowledge that EF Core exists - Dependency Inversion the
///     compiler can check.
///   </item>
/// </list>
/// <para>
/// Every method takes a <see cref="CancellationToken"/>, and that is not
/// boilerplate: when a client abandons a request, the token lets the database
/// command be cancelled instead of running to completion for a response nobody
/// will read.
/// </para>
/// </remarks>
public interface IBookRepository
{
    /// <summary>Searches, filters, sorts, and pages the catalogue.</summary>
    Task<PagedResult<BookSummaryDto>> SearchAsync(
        BookSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Full detail for one book, including its copies. Null when not found.</summary>
    Task<BookDetailDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Full detail by ISBN. Null when not found.</summary>
    Task<BookDetailDto?> GetByIsbnAsync(string isbn, CancellationToken cancellationToken = default);

    /// <summary>Copies belonging to one book.</summary>
    Task<IReadOnlyList<BookCopyDto>> GetCopiesAsync(
        int bookId,
        CancellationToken cancellationToken = default);

    /// <summary>True when a book with this id exists, without loading it.</summary>
    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>True when a book with this ISBN already exists.</summary>
    Task<bool> IsbnExistsAsync(string isbn, CancellationToken cancellationToken = default);
}
