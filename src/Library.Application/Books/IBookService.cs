using Library.Application.Books.Dtos;
using Library.Application.Common.Models;

namespace Library.Application.Books;

/// <summary>
/// Catalogue use cases, as the API layer sees them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a service on top of the repository, when the repository already has
/// these methods?</b> Because they answer different questions. The repository
/// answers "what is in the database" and returns <c>null</c> for a miss. The
/// service answers "what should happen when a librarian asks for book 42" - and
/// the answer to a miss is a <see cref="Domain.Exceptions.NotFoundException"/>,
/// which the API layer turns into a 404.
/// </para>
/// <para>
/// Keeping that decision here rather than in the controller means every caller
/// gets the same behaviour, and the controller stays a thin translation layer
/// between HTTP and the domain. In Phase 4 this is also where the multi-step
/// workflows live - issue a loan, assess a fine - which have no natural home in
/// a repository at all.
/// </para>
/// </remarks>
public interface IBookService
{
    /// <summary>Searches the catalogue. An empty result is success, not an error.</summary>
    Task<PagedResult<BookSummaryDto>> SearchAsync(
        BookSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one book by id.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No book with this id.</exception>
    Task<BookDetailDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Gets one book by ISBN.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No book with this ISBN.</exception>
    Task<BookDetailDto> GetByIsbnAsync(string isbn, CancellationToken cancellationToken = default);

    /// <summary>Lists the physical copies of a book.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No book with this id.</exception>
    Task<IReadOnlyList<BookCopyDto>> GetCopiesAsync(
        int bookId,
        CancellationToken cancellationToken = default);
}
