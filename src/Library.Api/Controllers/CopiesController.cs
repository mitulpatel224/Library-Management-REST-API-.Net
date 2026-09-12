using Library.Application.Books;
using Library.Application.Books.Dtos;
using Library.Application.Books.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Controllers;

/// <summary>
/// Operations on an individual physical copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a top-level route rather than nesting under the book.</b> Listing and
/// adding copies are nested (<c>/api/books/{id}/copies</c>) because those
/// operations are meaningless without knowing which title they concern.
/// </para>
/// <para>
/// Addressing one copy is different: a barcode identifies a physical item
/// uniquely across the whole library, so a caller holding a copy id already
/// knows everything needed. Forcing <c>/api/books/7/copies/42</c> would make the
/// client look up a book id it does not have — and would invite the bug where
/// copy 42 does not actually belong to book 7.
/// </para>
/// <para>
/// The rule: nest while the parent is needed to identify the child; stop nesting
/// once the child identifies itself.
/// </para>
/// </remarks>
[ApiController]
[Route("api/copies")]
[Produces("application/json")]
public sealed class CopiesController : ControllerBase
{
    private readonly IBookService _bookService;

    public CopiesController(IBookService bookService) => _bookService = bookService;

    /// <summary>Updates a copy's condition or shelf location.</summary>
    /// <remarks>
    /// Status is deliberately not settable here. A copy becomes <c>OnLoan</c> by
    /// being issued and <c>Available</c> by being returned — letting a client
    /// set it directly would allow a copy to be marked available while a member
    /// still holds it, desynchronising it from the loan records that are the
    /// actual source of truth.
    /// </remarks>
    /// <param name="id">The copy's id.</param>
    /// <param name="request">New condition and location.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The updated copy.</response>
    /// <response code="404">No copy with this id exists.</response>
    /// <response code="422">Validation failed.</response>
    [HttpPut("{id:int}", Name = "UpdateCopy")]
    [ProducesResponseType<BookCopyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookCopyDto>> Update(
        int id,
        [FromBody] UpdateBookCopyRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _bookService.UpdateCopyAsync(id, request, cancellationToken));
    }

    /// <summary>Removes a physical copy.</summary>
    /// <remarks>Refused while the copy is on loan.</remarks>
    /// <param name="id">The copy's id.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">No copy with this id exists.</response>
    /// <response code="409">The copy is currently on loan.</response>
    [HttpDelete("{id:int}", Name = "DeleteCopy")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await _bookService.DeleteCopyAsync(id, cancellationToken);

        return NoContent();
    }
}
