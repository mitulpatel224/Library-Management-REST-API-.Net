using System.Linq.Expressions;
using Library.Application.Books;
using Library.Application.Books.Dtos;
using Library.Application.Common.Models;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Domain.ValueObjects;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of the catalogue read model.
/// </summary>
/// <remarks>
/// This is the only class in the solution that knows the catalogue is stored in
/// a relational database. Everything above it sees <see cref="IBookRepository"/>.
/// </remarks>
public sealed class BookRepository : IBookRepository
{
    private readonly LibraryDbContext _context;

    public BookRepository(LibraryDbContext context) => _context = context;

    /// <summary>
    /// Searches, filters, sorts, and pages the catalogue in a single round trip
    /// per page (plus one for the count).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole method is one deferred query.</b> Nothing touches the
    /// database until <c>CountAsync</c> and <c>ToListAsync</c> at the end.
    /// <c>IQueryable</c> builds an expression tree; each conditional
    /// <c>Where</c> below appends to that tree rather than filtering anything.
    /// The result is one SQL statement with exactly the predicates the caller
    /// asked for - not a full table read followed by in-memory filtering.
    /// </para>
    /// <para>
    /// <b>Watch the <c>if</c> statements.</b> That pattern is the point: a filter
    /// that was not supplied contributes no SQL at all. An unfiltered listing
    /// generates a clean <c>SELECT ... ORDER BY ... LIMIT</c>, not a query
    /// padded with <c>(@p IS NULL OR col = @p)</c> clauses that defeat index use.
    /// </para>
    /// </remarks>
    public async Task<PagedResult<BookSummaryDto>> SearchAsync(
        BookSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        BookSearchRequest normalized = (BookSearchRequest)request.Normalize();

        // AsNoTracking: this is a read. Change tracking would have EF Core take a
        // snapshot of every entity so it can detect edits later - pure overhead
        // for data that is about to be projected and discarded.
        IQueryable<Book> query = _context.Books.AsNoTracking();

        query = ApplyFilters(query, normalized);

        // COUNT runs against the filtered query but WITHOUT the ordering or
        // paging, which is what makes TotalCount mean "matches" rather than
        // "matches on this page".
        int totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult.Empty<BookSummaryDto>(normalized.Page, normalized.PageSize);
        }

        query = ApplySorting(query, normalized);

        // ---------------------------------------------------------------
        // Projection.
        //
        // Select BEFORE ToList, so EF Core translates this shape into the SQL
        // column list and the joins for Authors/Genres. Two consequences:
        //
        //   1. No N+1. The author names arrive with the page, not via one extra
        //      query per book.
        //   2. No over-fetching. Description and PageCount are never read here,
        //      because this DTO does not mention them.
        //
        // Calling ToListAsync first and mapping afterwards would lose both.
        // ---------------------------------------------------------------
        List<BookSummaryDto> items = await query
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .Select(book => new BookSummaryDto
            {
                Id = book.Id,
                Isbn = book.Isbn,
                Title = book.Title,
                Subtitle = book.Subtitle,
                CategoryName = book.Category.Name,
                PublisherName = book.Publisher != null ? book.Publisher.Name : null,
                PublishedOn = book.PublishedOn,
                Authors = book.BookAuthors
                    .OrderBy(ba => ba.AuthorOrder)
                    .Select(ba => ba.Author.FirstName + " " + ba.Author.LastName)
                    .ToList(),
                Genres = book.BookGenres
                    .Select(bg => bg.Genre.Name)
                    .ToList(),
                TotalCopies = book.Copies.Count,

                // Counted in SQL, not by loading every copy. This is why
                // Book.AvailableCopies (which counts a loaded collection) is not
                // used on the listing path.
                AvailableCopies = book.Copies.Count(c => c.Status == CopyStatus.Available),
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<BookSummaryDto>(
            items, normalized.Page, normalized.PageSize, totalCount);
    }

    /// <summary>
    /// Composes the requested filters onto the query.
    /// </summary>
    /// <remarks>
    /// Each predicate is a lambda compiled into an expression tree and appended
    /// to the query. Extracted from <see cref="SearchAsync"/> so the filtering
    /// rules can be read - and changed - without wading through paging and
    /// projection concerns. Single Responsibility applied at method scope.
    /// </remarks>
    private static IQueryable<Book> ApplyFilters(IQueryable<Book> query, BookSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string term = request.Search.Trim();

            // ---------------------------------------------------------------
            // EF.Functions.Like translates to SQL LIKE. The term is still passed
            // as a parameter - the wildcards are part of the VALUE, not the
            // statement - so this is not an injection vector.
            //
            // Book.Isbn is an ordinary mapped string, so LIKE works on it
            // exactly as it does on Title. See Book.Isbn for why the value
            // object is deliberately not the mapped property.
            // ---------------------------------------------------------------
            query = query.Where(b =>
                EF.Functions.Like(b.Title, $"%{term}%") ||
                (b.Subtitle != null && EF.Functions.Like(b.Subtitle, $"%{term}%")) ||
                EF.Functions.Like(b.Isbn, $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(request.Isbn))
        {
            // Normalised the same way the domain normalises on write, so a
            // hyphenated query matches an unhyphenated stored value.
            string isbn = new([.. request.Isbn.Where(char.IsAsciiDigit)]);

            if (isbn.Length > 0)
            {
                // Plain string equality against the indexed column - an index
                // seek on IX_Books_Isbn.
                query = query.Where(b =>
                    b.Isbn == isbn);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            string author = request.Author.Trim();

            // Any() over the join becomes an EXISTS subquery - it does not
            // duplicate book rows the way a naive join would.
            query = query.Where(b => b.BookAuthors.Any(ba =>
                EF.Functions.Like(ba.Author.FirstName, $"%{author}%") ||
                EF.Functions.Like(ba.Author.LastName, $"%{author}%")));
        }

        if (request.CategoryId is > 0)
        {
            query = query.Where(b => b.CategoryId == request.CategoryId);
        }

        if (request.GenreId is > 0)
        {
            query = query.Where(b => b.BookGenres.Any(bg => bg.GenreId == request.GenreId));
        }

        if (request.PublisherId is > 0)
        {
            query = query.Where(b => b.PublisherId == request.PublisherId);
        }

        if (request.PublishedFrom is not null)
        {
            query = query.Where(b => b.PublishedOn >= request.PublishedFrom);
        }

        if (request.PublishedTo is not null)
        {
            query = query.Where(b => b.PublishedOn <= request.PublishedTo);
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            string language = request.Language.Trim();
            query = query.Where(b => b.Language == language);
        }

        if (request.AvailableOnly)
        {
            // "At least one copy on the shelf" - an EXISTS, which stops at the
            // first match rather than counting them all.
            query = query.Where(b => b.Copies.Any(c => c.Status == CopyStatus.Available));
        }

        return query;
    }

    /// <summary>
    /// Applies the whitelisted sort, then a tiebreaker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>ThenBy(b =&gt; b.Id)</c> is not decoration.</b> SQL guarantees no
    /// ordering among rows that tie on the ORDER BY key, so with fifty books
    /// published in 2024 and <c>sortBy=published</c>, the database may return
    /// them in any order - and a different order on the next call. Page 2 would
    /// then repeat or skip rows that page 1 already showed. A unique tiebreaker
    /// makes the total ordering deterministic and paging stable.
    /// </para>
    /// </remarks>
    private static IQueryable<Book> ApplySorting(IQueryable<Book> query, BookSearchRequest request)
    {
        // Never the caller's string - always the expression it maps to.
        Expression<Func<Book, object?>> sortExpression = BookSortOptions.Resolve(request.SortBy);

        IOrderedQueryable<Book> ordered = request.IsDescending
            ? query.OrderByDescending(sortExpression)
            : query.OrderBy(sortExpression);

        return ordered.ThenBy(b => b.Id);
    }

    public async Task<BookDetailDto?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await ProjectDetail(_context.Books.AsNoTracking().Where(b => b.Id == id))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<BookDetailDto?> GetByIsbnAsync(
        string isbn,
        CancellationToken cancellationToken = default)
    {
        string normalized = new([.. (isbn ?? string.Empty).Where(char.IsAsciiDigit)]);

        if (normalized.Length == 0)
        {
            return null;
        }

        return await ProjectDetail(
                _context.Books.AsNoTracking()
                    .Where(b => b.Isbn == normalized))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Shared detail projection, so <c>GetById</c> and <c>GetByIsbn</c> cannot
    /// drift apart.
    /// </summary>
    /// <remarks>
    /// Returning <c>IQueryable</c> here is safe because the method is private and
    /// both callers immediately materialise it. The rule about not leaking
    /// <c>IQueryable</c> applies at the layer boundary, not inside a class.
    /// </remarks>
    private static IQueryable<BookDetailDto> ProjectDetail(IQueryable<Book> query) =>
        query.Select(book => new BookDetailDto
        {
            Id = book.Id,
            Isbn = book.Isbn,
            Title = book.Title,
            Subtitle = book.Subtitle,
            Description = book.Description,
            CategoryId = book.CategoryId,
            CategoryName = book.Category.Name,
            PublisherId = book.PublisherId,
            PublisherName = book.Publisher != null ? book.Publisher.Name : null,
            PublishedOn = book.PublishedOn,
            Language = book.Language,
            PageCount = book.PageCount,
            Authors = book.BookAuthors
                .OrderBy(ba => ba.AuthorOrder)
                .Select(ba => new AuthorSummaryDto
                {
                    Id = ba.AuthorId,
                    FullName = ba.Author.FirstName + " " + ba.Author.LastName,
                    AuthorOrder = ba.AuthorOrder,
                    Role = ba.Role,
                })
                .ToList(),
            Genres = book.BookGenres
                .Select(bg => new GenreSummaryDto
                {
                    Id = bg.GenreId,
                    Name = bg.Genre.Name,
                    Slug = bg.Genre.Slug,
                })
                .ToList(),
            Copies = book.Copies
                .OrderBy(c => c.Barcode)
                .Select(c => new BookCopyDto
                {
                    Id = c.Id,
                    BookId = c.BookId,
                    Barcode = c.Barcode,
                    Status = c.Status,
                    Condition = c.Condition,
                    ShelfLocation = c.ShelfLocation,
                    AcquiredOn = c.AcquiredOn,
                    IsAvailable = c.Status == CopyStatus.Available,
                })
                .ToList(),
            TotalCopies = book.Copies.Count,
            AvailableCopies = book.Copies.Count(c => c.Status == CopyStatus.Available),
            CreatedAt = book.CreatedAt,
            UpdatedAt = book.UpdatedAt,
        });

    public async Task<IReadOnlyList<BookCopyDto>> GetCopiesAsync(
        int bookId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookCopies
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Barcode)
            .Select(c => new BookCopyDto
            {
                Id = c.Id,
                BookId = c.BookId,
                Barcode = c.Barcode,
                Status = c.Status,
                Condition = c.Condition,
                ShelfLocation = c.ShelfLocation,
                AcquiredOn = c.AcquiredOn,
                IsAvailable = c.Status == CopyStatus.Available,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        // AnyAsync compiles to SELECT EXISTS(...) - the database answers without
        // reading the row, and EF Core never materialises an entity.
        return await _context.Books
            .AsNoTracking()
            .AnyAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<bool> IsbnExistsAsync(
        string isbn,
        CancellationToken cancellationToken = default)
    {
        string normalized = new([.. (isbn ?? string.Empty).Where(char.IsAsciiDigit)]);

        if (normalized.Length == 0)
        {
            return false;
        }

        // AnyAsync stops at the first hit and returns a boolean - it never
        // materialises the row. CountAsync() > 0 would scan every match.
        return await _context.Books
            .AsNoTracking()
            .AnyAsync(
                b => b.Isbn == normalized,
                cancellationToken);
    }

    // =======================================================================
    // Write operations
    // =======================================================================

    /// <summary>
    /// Loads a book with everything a write use case needs to mutate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Note the absence of <c>AsNoTracking</c> — the exact opposite of the read
    /// path. Change tracking is what lets a caller mutate the entity and have
    /// <c>SaveChanges</c> work out the UPDATE, and it is what makes
    /// <c>SetAuthors</c> clearing the collection translate into DELETEs on the
    /// join table.
    /// </para>
    /// <para>
    /// <c>AsSplitQuery</c> because three collection <c>Include</c>s in one
    /// statement produce a cartesian explosion: a book with 4 authors, 2 genres
    /// and 5 copies returns 4 × 2 × 5 = 40 rows, each repeating every book
    /// column. Split queries issue one statement per collection instead. The
    /// trade-off is that the reads are no longer in a single snapshot, which is
    /// irrelevant here because the caller is about to take a write lock anyway.
    /// </para>
    /// </remarks>
    public async Task<Book?> GetEntityAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Books
            .Include(b => b.BookAuthors)
            .Include(b => b.BookGenres)
            .Include(b => b.Copies)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    /// <summary>
    /// Loads a tracked book by ISBN, with its join collections, for the import
    /// update path.
    /// </summary>
    /// <remarks>
    /// <c>Include</c>s the joins because <c>SetAuthors</c> clears and rebuilds
    /// them — without the existing rows loaded, EF Core has nothing to delete and
    /// the old credits survive alongside the new ones.
    /// </remarks>
    public async Task<Book?> GetEntityByIsbnAsync(
        string isbn,
        CancellationToken cancellationToken = default)
    {
        string normalized = new([.. (isbn ?? string.Empty).Where(char.IsAsciiDigit)]);

        if (normalized.Length == 0)
        {
            return null;
        }

        return await _context.Books
            .Include(b => b.BookAuthors)
            .Include(b => b.BookGenres)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Isbn == normalized, cancellationToken);
    }

    public async Task<BookCopy?> GetCopyEntityAsync(
        int copyId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BookCopies
            .FirstOrDefaultAsync(c => c.Id == copyId, cancellationToken);
    }

    // These only stage changes in the change tracker. Nothing reaches the
    // database until IUnitOfWork.SaveChangesAsync is called, which is what lets
    // one use case span several mutations in a single transaction.
    public void Add(Book book) => _context.Books.Add(book);

    public void Remove(Book book) => _context.Books.Remove(book);

    public void RemoveCopy(BookCopy copy) => _context.BookCopies.Remove(copy);

    public async Task<bool> CategoryExistsAsync(
        int categoryId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Categories
            .AsNoTracking()
            .AnyAsync(c => c.Id == categoryId, cancellationToken);
    }

    public async Task<bool> PublisherExistsAsync(
        int publisherId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Publishers
            .AsNoTracking()
            .AnyAsync(p => p.Id == publisherId, cancellationToken);
    }

    /// <summary>
    /// Finds which of the supplied author ids do not exist.
    /// </summary>
    /// <remarks>
    /// One query for the whole set, not one per id. The database returns the ids
    /// that DO exist and the difference is computed in memory over a handful of
    /// integers — cheaper and simpler than expressing "not in" per element.
    /// <para>
    /// Reporting every missing id at once means the caller fixes all of them in
    /// one round trip, rather than discovering them one failed request at a time.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<int>> FindMissingAuthorIdsAsync(
        IReadOnlyCollection<int> authorIds,
        CancellationToken cancellationToken = default)
    {
        if (authorIds is null || authorIds.Count == 0)
        {
            return [];
        }

        List<int> existing = await _context.Authors
            .AsNoTracking()
            .Where(a => authorIds.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        return [.. authorIds.Except(existing)];
    }

    public async Task<bool> BarcodeExistsAsync(
        string barcode,
        int? excludeCopyId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return false;
        }

        // Upper-cased to match BookCopy.Create/UpdateDetails, which normalise on
        // the way in - otherwise a re-label differing only in case would slip
        // past this check and then fail at the index.
        string normalized = barcode.Trim().ToUpperInvariant();

        IQueryable<BookCopy> query = _context.BookCopies.AsNoTracking();

        if (excludeCopyId is > 0)
        {
            // Exclude the row being edited, so keeping a barcode unchanged does
            // not conflict with itself.
            query = query.Where(c => c.Id != excludeCopyId);
        }

        return await query.AnyAsync(c => c.Barcode == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<int>> FindMissingGenreIdsAsync(
        IReadOnlyCollection<int> genreIds,
        CancellationToken cancellationToken = default)
    {
        if (genreIds is null || genreIds.Count == 0)
        {
            return [];
        }

        List<int> existing = await _context.Genres
            .AsNoTracking()
            .Where(g => genreIds.Contains(g.Id))
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        return [.. genreIds.Except(existing)];
    }
}
