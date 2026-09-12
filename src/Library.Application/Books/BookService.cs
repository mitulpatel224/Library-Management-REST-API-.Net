using Library.Application.Books.Dtos;
using Library.Application.Books.Requests;
using Library.Application.Common.Abstractions;
using Library.Application.Common.Models;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.Application.Books;

/// <summary>
/// Catalogue use cases. Turns "not in the database" into "404 for the caller",
/// and orchestrates writes so that each use case commits exactly once.
/// </summary>
public sealed class BookService : IBookService
{
    private readonly IBookRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    // Constructor injection of INTERFACES, not the EF Core implementations.
    // That is what lets this class be unit-tested with substitutes and no
    // database at all.
    public BookService(IBookRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

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

    // =======================================================================
    // Write use cases
    // =======================================================================

    /// <summary>
    /// Catalogues a new book.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation happens at three levels, and each catches something the others
    /// cannot:
    /// </para>
    /// <list type="number">
    ///   <item><b>The validator</b> (before this method) — shape and format.
    ///     Needs no database.</item>
    ///   <item><b>This method</b> — cross-entity rules that need a lookup:
    ///     does the ISBN already exist, do the referenced ids exist. Produces
    ///     good error messages.</item>
    ///   <item><b>The database</b> — unique indexes and foreign keys. The only
    ///     level that holds under concurrency.</item>
    /// </list>
    /// <para>
    /// The ISBN check below is level 2. Two simultaneous requests can both pass
    /// it; the unique index settles which one wins, and <c>UnitOfWork</c> turns
    /// the loser's constraint violation into a 409. The check is for the
    /// message, the index is the guarantee.
    /// </para>
    /// </remarks>
    public async Task<BookDetailDto> CreateAsync(
        CreateBookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Parsed through the value object, so the check digit is verified and
        // hyphens are normalised away before anything touches the database.
        Isbn isbn = Isbn.Create(request.Isbn);

        if (await _repository.IsbnExistsAsync(isbn.Value, cancellationToken))
        {
            throw new ConflictException(
                "book.duplicate_isbn",
                $"A book with ISBN {isbn.ToDisplayString()} is already catalogued.");
        }

        await EnsureReferencesExistAsync(
            request.CategoryId, request.PublisherId,
            request.AuthorIds, request.GenreIds, cancellationToken);

        Book book = Book.Create(
            isbn,
            request.Title,
            request.CategoryId,
            request.Subtitle,
            request.PublisherId,
            request.PublishedOn,
            request.Language,
            request.PageCount,
            request.Description);

        _repository.Add(book);

        // Committed before the joins are set, because BookAuthor rows carry the
        // book's key and the database assigns it on insert.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (request.AuthorIds.Count > 0 || request.GenreIds.Count > 0)
        {
            book.SetAuthors(request.AuthorIds);
            book.SetGenres(request.GenreIds);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await GetByIdAsync(book.Id, cancellationToken);
    }

    public async Task<BookDetailDto> UpdateAsync(
        int id,
        UpdateBookRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Book book = await _repository.GetEntityAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Book", id);

        await EnsureReferencesExistAsync(
            request.CategoryId, request.PublisherId,
            request.AuthorIds, request.GenreIds, cancellationToken);

        // All mutation goes through the entity's own methods, so the rules
        // encoded there (title required, page count positive, no duplicate
        // author) are enforced no matter which caller arrives.
        book.UpdateDetails(
            request.Title,
            request.Subtitle,
            request.CategoryId,
            request.PublisherId,
            request.PublishedOn,
            request.Language,
            request.PageCount,
            request.Description);

        book.SetAuthors(request.AuthorIds);
        book.SetGenres(request.GenreIds);

        // One commit for every change above - a single transaction. Saving
        // inside each repository call would produce four, and a failure partway
        // would leave the book half-updated.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    /// <summary>
    /// Deletes a book and its copies.
    /// </summary>
    /// <remarks>
    /// Refuses while any copy is on loan. The database would happily cascade the
    /// delete and take the copy rows with it, leaving a member holding a book
    /// the system has no record of — the kind of silent data loss a foreign key
    /// cannot protect against, because the loan is a legitimate row pointing at
    /// a legitimately deletable one.
    /// </remarks>
    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        Book book = await _repository.GetEntityAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Book", id);

        List<string> onLoan = [.. book.Copies
            .Where(c => c.Status == CopyStatus.OnLoan)
            .Select(c => c.Barcode)];

        if (onLoan.Count > 0)
        {
            throw new ConflictException(
                "book.has_copies_on_loan",
                $"Cannot delete '{book.Title}': {onLoan.Count} " +
                $"cop{(onLoan.Count == 1 ? "y is" : "ies are")} currently on loan " +
                $"({string.Join(", ", onLoan)}).");
        }

        _repository.Remove(book);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<BookCopyDto> AddCopyAsync(
        int bookId,
        AddBookCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Book book = await _repository.GetEntityAsync(bookId, cancellationToken)
                    ?? throw new NotFoundException("Book", bookId);

        // Three layers again, and each catches what the others cannot:
        //
        //   1. This check - barcode already used ANYWHERE in the library. The
        //      entity cannot see it, because Book.AddCopy only knows its own
        //      copies. Produces the useful message naming the barcode.
        //   2. Book.AddCopy - duplicate within this title.
        //   3. IX_BookCopies_Barcode - the guarantee under concurrency, which
        //      UnitOfWork translates into a 409.
        if (await _repository.BarcodeExistsAsync(request.Barcode, cancellationToken: cancellationToken))
        {
            throw new ConflictException(
                "copy.duplicate_barcode",
                $"Barcode '{request.Barcode.Trim().ToUpperInvariant()}' is already in use.");
        }

        BookCopy copy = book.AddCopy(
            request.Barcode,
            request.Condition,
            request.ShelfLocation,
            request.AcquiredOn);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToDto(copy);
    }

    public async Task<BookCopyDto> UpdateCopyAsync(
        int copyId,
        UpdateBookCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        BookCopy copy = await _repository.GetCopyEntityAsync(copyId, cancellationToken)
                        ?? throw new NotFoundException("Copy", copyId);

        // Excluding this copy matters: without it, a request that leaves the
        // barcode unchanged would match the row being edited and report a false
        // conflict on every ordinary condition or shelf-location update.
        if (await _repository.BarcodeExistsAsync(request.Barcode, copyId, cancellationToken))
        {
            throw new ConflictException(
                "copy.duplicate_barcode",
                $"Barcode '{request.Barcode.Trim().ToUpperInvariant()}' is already in use " +
                "by another copy.");
        }

        copy.UpdateDetails(request.Barcode, request.Condition, request.ShelfLocation);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToDto(copy);
    }

    public async Task DeleteCopyAsync(int copyId, CancellationToken cancellationToken = default)
    {
        BookCopy copy = await _repository.GetCopyEntityAsync(copyId, cancellationToken)
                        ?? throw new NotFoundException("Copy", copyId);

        if (copy.Status == CopyStatus.OnLoan)
        {
            throw new ConflictException(
                "copy.on_loan",
                $"Copy '{copy.Barcode}' cannot be deleted while it is on loan.");
        }

        _repository.RemoveCopy(copy);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Verifies every foreign key the request references, reporting all failures
    /// at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a bad category id surfaces as a foreign-key violation — a
    /// 500, or at best an opaque "constraint failed". Checking first turns it
    /// into a 422 that names the offending id.
    /// </para>
    /// <para>
    /// Reporting <b>all</b> missing ids in one response, rather than failing on
    /// the first, means the caller fixes everything in one round trip instead of
    /// discovering problems one rejected request at a time.
    /// </para>
    /// </remarks>
    private async Task EnsureReferencesExistAsync(
        int categoryId,
        int? publisherId,
        IReadOnlyList<int> authorIds,
        IReadOnlyList<int> genreIds,
        CancellationToken cancellationToken)
    {
        if (!await _repository.CategoryExistsAsync(categoryId, cancellationToken))
        {
            throw new BusinessRuleViolationException(
                "book.category_not_found", $"Category {categoryId} does not exist.");
        }

        if (publisherId is > 0 &&
            !await _repository.PublisherExistsAsync(publisherId.Value, cancellationToken))
        {
            throw new BusinessRuleViolationException(
                "book.publisher_not_found", $"Publisher {publisherId} does not exist.");
        }

        IReadOnlyList<int> missingAuthors =
            await _repository.FindMissingAuthorIdsAsync(authorIds, cancellationToken);

        if (missingAuthors.Count > 0)
        {
            throw new BusinessRuleViolationException(
                "book.author_not_found",
                $"These author ids do not exist: {string.Join(", ", missingAuthors)}.");
        }

        IReadOnlyList<int> missingGenres =
            await _repository.FindMissingGenreIdsAsync(genreIds, cancellationToken);

        if (missingGenres.Count > 0)
        {
            throw new BusinessRuleViolationException(
                "book.genre_not_found",
                $"These genre ids do not exist: {string.Join(", ", missingGenres)}.");
        }
    }

    private static BookCopyDto ToDto(BookCopy copy) => new()
    {
        Id = copy.Id,
        BookId = copy.BookId,
        Barcode = copy.Barcode,
        Status = copy.Status,
        Condition = copy.Condition,
        ShelfLocation = copy.ShelfLocation,
        AcquiredOn = copy.AcquiredOn,
        IsAvailable = copy.IsAvailable,
    };
}
