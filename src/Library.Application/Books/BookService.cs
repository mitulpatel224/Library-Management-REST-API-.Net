using Library.Application.Books.Dtos;
using Library.Application.Common.Models;
using Library.Domain.Exceptions;

namespace Library.Application.Books;

/// <summary>
/// Catalogue use cases. Turns "not in the database" into "404 for the caller".
/// </summary>
public sealed class BookService : IBookService
{
    private readonly IBookRepository _repository;

    // Constructor injection of the INTERFACE, not the EF Core implementation.
    // That is what lets this class be unit-tested with a substitute repository
    // and no database at all.
    public BookService(IBookRepository repository) => _repository = repository;

    public Task<PagedResult<BookSummaryDto>> SearchAsync(
        BookSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No await, no try/catch, nothing to add - so the Task is returned
        // directly rather than awaited and re-wrapped. One less state machine.
        //
        // An empty page is a legitimate answer to a search. Only a request for a
        // *specific* resource can be "not found".
        return _repository.SearchAsync(request, cancellationToken);
    }

    public async Task<BookDetailDto> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        BookDetailDto? book = await _repository.GetByIdAsync(id, cancellationToken);

        // The null-to-exception translation this class exists for. The
        // alternative - returning null and having every controller remember to
        // check - works right up until one of them forgets.
        return book ?? throw new NotFoundException("Book", id);
    }

    public async Task<BookDetailDto> GetByIsbnAsync(
        string isbn,
        CancellationToken cancellationToken = default)
    {
        BookDetailDto? book = await _repository.GetByIsbnAsync(isbn, cancellationToken);

        return book ?? throw new NotFoundException("Book", isbn);
    }

    public async Task<IReadOnlyList<BookCopyDto>> GetCopiesAsync(
        int bookId,
        CancellationToken cancellationToken = default)
    {
        // Distinguishes "this book has no copies" (empty list, 200) from "there
        // is no such book" (404). Returning an empty list for both would tell
        // the caller the book exists when it does not.
        if (!await _repository.ExistsAsync(bookId, cancellationToken))
        {
            throw new NotFoundException("Book", bookId);
        }

        return await _repository.GetCopiesAsync(bookId, cancellationToken);
    }
}
