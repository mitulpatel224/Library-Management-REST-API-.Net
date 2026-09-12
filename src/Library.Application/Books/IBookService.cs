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

    // -----------------------------------------------------------------------
    // Write use cases
    // -----------------------------------------------------------------------

    /// <summary>Catalogues a new book.</summary>
    /// <exception cref="Domain.Exceptions.ConflictException">A book with this ISBN exists.</exception>
    /// <exception cref="Domain.Exceptions.BusinessRuleViolationException">
    /// A referenced category, publisher, author or genre does not exist.
    /// </exception>
    Task<BookDetailDto> CreateAsync(
        Requests.CreateBookRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a book's details, authors and genres.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No book with this id.</exception>
    Task<BookDetailDto> UpdateAsync(
        int id,
        Requests.UpdateBookRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a book and all of its copies.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No book with this id.</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">A copy is on loan.</exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Adds a physical copy to a book.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No book with this id.</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">The barcode is already in use.</exception>
    Task<BookCopyDto> AddCopyAsync(
        int bookId,
        Requests.AddBookCopyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Updates a copy's condition or shelf location.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No copy with this id.</exception>
    Task<BookCopyDto> UpdateCopyAsync(
        int copyId,
        Requests.UpdateBookCopyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a physical copy.</summary>
    /// <exception cref="Domain.Exceptions.NotFoundException">No copy with this id.</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">The copy is on loan.</exception>
    Task DeleteCopyAsync(int copyId, CancellationToken cancellationToken = default);
}
