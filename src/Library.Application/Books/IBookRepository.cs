using Library.Application.Books.Dtos;
using Library.Application.Common.Models;
using Library.Domain.Entities;

namespace Library.Application.Books;

/// <summary>
/// Read and write access to the book catalogue.
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

    // -----------------------------------------------------------------------
    // Write operations.
    //
    // These return DOMAIN ENTITIES rather than DTOs, and that is correct: a
    // write use case must invoke the entity's own methods (UpdateDetails,
    // AddCopy, SetAuthors) so the invariants encoded there are enforced. A DTO
    // has no behaviour to invoke.
    //
    // The rule that still holds is the one about IQueryable - that never
    // escapes. Entities do, because Application references Domain by design.
    //
    // None of these commit. Committing is IUnitOfWork's job, so a single use
    // case can make several changes inside one transaction.
    // -----------------------------------------------------------------------

    /// <summary>Loads a tracked book, with authors, genres and copies, for modification.</summary>
    Task<Book?> GetEntityAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Loads a tracked book by ISBN, for the import update path.</summary>
    /// <remarks>
    /// Distinct from <c>GetByIsbnAsync</c>, which returns a DTO for reading. This
    /// returns the tracked entity, because the importer needs to invoke
    /// <c>UpdateDetails</c> and <c>SetAuthors</c> on it.
    /// </remarks>
    Task<Book?> GetEntityByIsbnAsync(string isbn, CancellationToken cancellationToken = default);

    /// <summary>Loads a tracked copy for modification.</summary>
    Task<BookCopy?> GetCopyEntityAsync(int copyId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new book for insertion.</summary>
    void Add(Book book);

    /// <summary>Stages a book for deletion. Its copies cascade.</summary>
    void Remove(Book book);

    /// <summary>Stages a single copy for deletion.</summary>
    void RemoveCopy(BookCopy copy);

    /// <summary>True when the category exists.</summary>
    Task<bool> CategoryExistsAsync(int categoryId, CancellationToken cancellationToken = default);

    /// <summary>True when the publisher exists.</summary>
    Task<bool> PublisherExistsAsync(int publisherId, CancellationToken cancellationToken = default);

    /// <summary>Returns the supplied author ids that do not exist.</summary>
    /// <remarks>
    /// One query for the whole set rather than one per id, and it reports every
    /// missing id at once — so a caller fixes all of them in a single round trip
    /// instead of discovering them one failed request at a time.
    /// </remarks>
    Task<IReadOnlyList<int>> FindMissingAuthorIdsAsync(
        IReadOnlyCollection<int> authorIds,
        CancellationToken cancellationToken = default);

    /// <summary>True when this barcode is already in use anywhere in the library.</summary>
    /// <remarks>
    /// <para>
    /// Book.AddCopy only checks within its own copies. Barcodes are unique
    /// LIBRARY-WIDE, so this covers the case the entity cannot see.
    /// </para>
    /// <para>
    /// <paramref name="excludeCopyId"/> is what makes this usable on an update:
    /// without it, saving a copy whose barcode has NOT changed would match
    /// itself and report a false conflict.
    /// </para>
    /// </remarks>
    Task<bool> BarcodeExistsAsync(
        string barcode,
        int? excludeCopyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the supplied genre ids that do not exist.</summary>
    Task<IReadOnlyList<int>> FindMissingGenreIdsAsync(
        IReadOnlyCollection<int> genreIds,
        CancellationToken cancellationToken = default);
}
