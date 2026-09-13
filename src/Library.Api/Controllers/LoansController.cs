using Library.Application.Loans;
using Library.Application.Loans.Dtos;
using Library.Application.Loans.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Controllers;

/// <summary>Lending — issuing copies to members and taking them back.</summary>
[ApiController]
[Route("api/loans")]
[Produces("application/json")]
public sealed class LoansController : ControllerBase
{
    private readonly ILoanService _loanService;

    public LoansController(ILoanService loanService) => _loanService = loanService;

    /// <summary>Gets one loan by id.</summary>
    /// <response code="200">The loan, with its fine if one was assessed.</response>
    /// <response code="404">No loan with this id exists.</response>
    [HttpGet("{id:int}", Name = "GetLoanById")]
    [ProducesResponseType<LoanDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanDetailDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await _loanService.GetByIdAsync(id, cancellationToken));
    }

    /// <summary>Issues a copy to a member.</summary>
    /// <remarks>
    /// <para>
    /// The due date is calculated by the server from the member's
    /// <c>MembershipType.LoanPeriodDays</c> — it cannot be supplied, or a caller
    /// could grant themselves a longer loan than their membership allows. The
    /// issue time comes from the server clock for the same reason: a back-dated
    /// issue is a way to avoid a fine.
    /// </para>
    /// <para>
    /// A <c>409</c> here means the copy is out. That answer is the same whether it
    /// was already out when the request arrived or was issued to someone else a
    /// moment earlier — the database's filtered unique index is what decides, and
    /// from the caller's side both mean "someone else has it".
    /// </para>
    /// </remarks>
    /// <response code="201">Created. The <c>Location</c> header points at the loan.</response>
    /// <response code="404">The copy or the member does not exist.</response>
    /// <response code="409">The copy is already on loan, or is not available.</response>
    /// <response code="422">
    /// Validation failed, the member may not borrow, or they are at their
    /// concurrent-loan limit.
    /// </response>
    [HttpPost("issue", Name = "IssueLoan")]
    [ProducesResponseType<LoanDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<LoanDetailDto>> Issue(
        [FromBody] IssueLoanRequest request,
        CancellationToken cancellationToken)
    {
        LoanDetailDto created = await _loanService.IssueAsync(request, cancellationToken);

        return CreatedAtRoute("GetLoanById", new { id = created.Id }, created);
    }
}
