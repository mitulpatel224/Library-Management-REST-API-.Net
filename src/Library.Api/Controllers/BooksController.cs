using Library.Application.Books;
using Library.Application.Books.Dtos;
using Library.Application.Books.Requests;
using Library.Application.Common.Models;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Controllers;

/// <summary>
/// Book catalogue endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Note how little this class does.</b> It binds HTTP to a method call and
/// turns the result into a status code. There is no business logic, no EF Core,
/// and - importantly - no try/catch: the <c>GlobalExceptionHandler</c> turns a
/// <c>NotFoundException</c> into a 404 for every action at once, so repeating
/// that here would be duplication that can only rot.
/// </para>
/// <para>
/// <c>[ApiController]</c> is doing real work too: it makes model binding errors
/// return an automatic 400 with a ProblemDetails body, infers <c>[FromBody]</c>
/// and <c>[FromQuery]</c>, and requires attribute routing. Without it, a
/// malformed request would silently arrive with default values rather than being
/// rejected.
/// </para>
/// </remarks>
[ApiController]
[Route("api/books")]
[Produces("application/json")]
public sealed class BooksController : ControllerBase
{
    private readonly IBookService _bookService;

    public BooksController(IBookService bookService) => _bookService = bookService;

    /// <summary>
    /// Searches the catalogue with filtering, sorting, and paging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every parameter is optional. With none supplied this returns the first
    /// page of the catalogue ordered by title.
    /// </para>
    /// <para>
    /// <b>Filters</b> combine with AND: <c>?categoryId=1&amp;availableOnly=true</c>
    /// returns available books in category 1.
    /// </para>
    /// <para>
    /// <b>Sorting</b> accepts only these fields - anything else falls back to
    /// title: <c>title</c>, <c>isbn</c>, <c>published</c>, <c>category</c>,
    /// <c>created</c>. Combine with <c>sortDir=asc|desc</c>.
    /// </para>
    /// <para>
    /// <b>Paging</b> is 1-based. <c>pageSize</c> is capped at 100 server-side.
    /// </para>
    /// </remarks>
    /// <param name="request">Filter, sort, and paging options, bound from the query string.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">A page of matching books. An empty page is a valid result.</response>
    [HttpGet(Name = "SearchBooks")]
    [ProducesResponseType<PagedResult<BookSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BookSummaryDto>>> Search(
        [FromQuery] BookSearchRequest request,
        CancellationToken cancellationToken)
    {
        PagedResult<BookSummaryDto> result =
            await _bookService.SearchAsync(request, cancellationToken);

        return Ok(result);
    }

    /// <summary>Gets one book by its id, including its physical copies.</summary>
    /// <param name="id">The book's surrogate key.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The book.</response>
    /// <response code="404">No book with this id exists.</response>
    [HttpGet("{id:int}", Name = "GetBookById")]
    [ProducesResponseType<BookDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookDetailDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        // No null check: the service throws NotFoundException, which the global
        // handler renders as a 404 ProblemDetails.
        BookDetailDto book = await _bookService.GetByIdAsync(id, cancellationToken);

        return Ok(book);
    }

    /// <summary>Gets one book by ISBN. Hyphenated and unhyphenated forms both work.</summary>
    /// <param name="isbn">ISBN-13, with or without hyphens.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The book.</response>
    /// <response code="404">No book with this ISBN exists.</response>
    [HttpGet("isbn/{isbn}", Name = "GetBookByIsbn")]
    [ProducesResponseType<BookDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookDetailDto>> GetByIsbn(
        string isbn,
        CancellationToken cancellationToken)
    {
        BookDetailDto book = await _bookService.GetByIsbnAsync(isbn, cancellationToken);

        return Ok(book);
    }

    /// <summary>Lists the physical copies of a book.</summary>
    /// <remarks>
    /// A sub-resource route, because a copy has no meaning independent of its
    /// title. An empty array means the book is catalogued but the library holds
    /// no physical copy of it - which is different from a 404.
    /// </remarks>
    /// <param name="id">The book's id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The copies, possibly none.</response>
    /// <response code="404">No book with this id exists.</response>
    [HttpGet("{id:int}/copies", Name = "GetBookCopies")]
    [ProducesResponseType<IReadOnlyList<BookCopyDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<BookCopyDto>>> GetCopies(
        int id,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BookCopyDto> copies =
            await _bookService.GetCopiesAsync(id, cancellationToken);

        return Ok(copies);
    }

    // =======================================================================
    // Writes
    // =======================================================================

    /// <summary>Catalogues a new book.</summary>
    /// <remarks>
    /// The ISBN may be sent hyphenated or not; it is normalised and its check
    /// digit verified. `authorIds` order becomes the credit order.
    /// </remarks>
    /// <param name="request">The book to create.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="201">Created. The <c>Location</c> header points at the new book.</response>
    /// <response code="409">A book with this ISBN is already catalogued.</response>
    /// <response code="422">Validation failed, or a referenced id does not exist.</response>
    [HttpPost(Name = "CreateBook")]
    [ProducesResponseType<BookDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookDetailDto>> Create(
        [FromBody] CreateBookRequest request,
        CancellationToken cancellationToken)
    {
        BookDetailDto created = await _bookService.CreateAsync(request, cancellationToken);

        // 201 with a Location header, per REST convention - the client learns
        // where the new resource lives without having to construct the URL.
        return CreatedAtRoute("GetBookById", new { id = created.Id }, created);
    }

    /// <summary>Replaces a book's details, authors and genres.</summary>
    /// <remarks>
    /// A full replacement, not a patch: the author and genre lists sent here
    /// become the complete lists. Sending the same body twice is idempotent.
    /// <para>
    /// The ISBN cannot be changed — it is the natural key, so altering it would
    /// make this a different book. Delete and re-create instead.
    /// </para>
    /// </remarks>
    /// <response code="200">The updated book.</response>
    /// <response code="404">No book with this id exists.</response>
    /// <response code="422">Validation failed, or a referenced id does not exist.</response>
    [HttpPut("{id:int}", Name = "UpdateBook")]
    [ProducesResponseType<BookDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookDetailDto>> Update(
        int id,
        [FromBody] UpdateBookRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _bookService.UpdateAsync(id, request, cancellationToken));
    }

    /// <summary>Removes a book and all of its copies.</summary>
    /// <remarks>
    /// Refused while any copy is on loan — the cascade would otherwise delete a
    /// copy a member is currently holding.
    /// </remarks>
    /// <response code="204">Deleted.</response>
    /// <response code="404">No book with this id exists.</response>
    /// <response code="409">One or more copies are on loan.</response>
    [HttpDelete("{id:int}", Name = "DeleteBook")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await _bookService.DeleteAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary>Adds a physical copy to a book.</summary>
    /// <remarks>Barcodes must be unique across the entire library, not just within this title.</remarks>
    /// <response code="201">The new copy.</response>
    /// <response code="404">No book with this id exists.</response>
    /// <response code="409">The barcode is already in use.</response>
    /// <response code="422">Validation failed.</response>
    [HttpPost("{id:int}/copies", Name = "AddBookCopy")]
    [ProducesResponseType<BookCopyDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookCopyDto>> AddCopy(
        int id,
        [FromBody] AddBookCopyRequest request,
        CancellationToken cancellationToken)
    {
        BookCopyDto copy = await _bookService.AddCopyAsync(id, request, cancellationToken);

        return CreatedAtRoute("GetBookCopies", new { id }, copy);
    }
}
