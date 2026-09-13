using Library.Application.Common.Models;
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

    /// <summary>Searches loans with filtering, sorting and paging.</summary>
    /// <remarks>
    /// <para>
    /// <c>status</c> accepts <c>Active</c>, <c>Overdue</c> or <c>Returned</c> and
    /// is resolved against the server clock — it is not a stored column, so it is
    /// never stale. <c>overdueOnly=true</c> is the chase list.
    /// </para>
    /// <para>
    /// <c>sortBy</c> accepts <c>due</c>, <c>issued</c>, <c>returned</c>,
    /// <c>member</c>, <c>title</c>, <c>barcode</c>; anything else falls back to
    /// <c>due</c>, which puts the most overdue loan first.
    /// </para>
    /// </remarks>
    /// <response code="200">A page of matching loans. An empty page is valid.</response>
    /// <response code="422">An inverted date range.</response>
    [HttpGet(Name = "SearchLoans")]
    [ProducesResponseType<PagedResult<LoanSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<LoanSummaryDto>>> Search(
        [FromQuery] LoanSearchRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _loanService.SearchAsync(request, cancellationToken));
    }

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

    /// <summary>Takes a copy back.</summary>
    /// <remarks>
    /// <para>
    /// The return time is the server's, not the caller's: whoever sets it decides
    /// the fine, and a caller who can set it can set it to the due date.
    /// </para>
    /// <para>
    /// If the copy is late, a fine is assessed by a handler that runs <b>after</b>
    /// this request's transaction commits. The fine therefore may not be present
    /// in the response body of this call — read the loan again, or
    /// <c>GET /api/fines?memberId=</c>, to see it. That ordering is deliberate: a
    /// rolled-back return must never leave a fine behind.
    /// </para>
    /// </remarks>
    /// <response code="200">The closed loan.</response>
    /// <response code="404">No loan with this id exists.</response>
    /// <response code="409">The loan was already returned.</response>
    /// <response code="422">The return date precedes the issue date.</response>
    [HttpPost("{id:int}/return", Name = "ReturnLoan")]
    [ProducesResponseType<LoanDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<LoanDetailDto>> Return(
        int id,
        [FromBody] ReturnLoanRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _loanService.ReturnAsync(id, request, cancellationToken));
    }

    /// <summary>Extends a loan.</summary>
    /// <remarks>
    /// <c>additionalDays</c> defaults to the member's own loan period — a Student
    /// gets another 28 days where a Standard member gets 14. Refused once the loan
    /// is overdue: renewing then would erase a fine that has already accrued,
    /// turning "return it late and renew" into a way of never paying.
    /// </remarks>
    /// <response code="200">The extended loan, with its new due date.</response>
    /// <response code="404">No loan with this id exists.</response>
    /// <response code="409">The loan is closed, or already overdue.</response>
    /// <response code="422">The renewal period is out of range.</response>
    [HttpPost("{id:int}/renew", Name = "RenewLoan")]
    [ProducesResponseType<LoanDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<LoanDetailDto>> Renew(
        int id,
        [FromBody] RenewLoanRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _loanService.RenewAsync(id, request, cancellationToken));
    }
}

/// <summary>Fines — what members owe for returning late, and settling it.</summary>
[ApiController]
[Route("api/fines")]
[Produces("application/json")]
public sealed class FinesController : ControllerBase
{
    private readonly IFineService _fineService;

    public FinesController(IFineService fineService) => _fineService = fineService;

    /// <summary>Searches fines.</summary>
    /// <remarks>
    /// <c>outstanding=true</c> returns only fines that are neither paid nor
    /// waived — the debt the library is actually owed.
    /// </remarks>
    /// <response code="200">A page of matching fines.</response>
    /// <response code="422">An inverted date range.</response>
    [HttpGet(Name = "SearchFines")]
    [ProducesResponseType<PagedResult<FineDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<FineDto>>> Search(
        [FromQuery] FineSearchRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _fineService.SearchAsync(request, cancellationToken));
    }

    /// <summary>Gets one fine by id.</summary>
    /// <response code="200">The fine.</response>
    /// <response code="404">No fine with this id exists.</response>
    [HttpGet("{id:int}", Name = "GetFineById")]
    [ProducesResponseType<FineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FineDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await _fineService.GetByIdAsync(id, cancellationToken));
    }

    /// <summary>Records payment of a fine in full.</summary>
    /// <remarks>
    /// Payment is all-or-nothing. Part payment would need an amount-paid column, a
    /// rule for overpayment, and a decision about whether a partly-paid fine still
    /// blocks borrowing — none of which is in scope, and all of which would be
    /// half-answered by accepting an amount here.
    /// </remarks>
    /// <response code="200">The settled fine.</response>
    /// <response code="404">No fine with this id exists.</response>
    /// <response code="409">Already paid, or already waived.</response>
    [HttpPost("{id:int}/pay", Name = "PayFine")]
    [ProducesResponseType<FineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FineDto>> Pay(
        int id,
        [FromBody] PayFineRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _fineService.PayAsync(id, request, cancellationToken));
    }

    /// <summary>Cancels a fine, recording why.</summary>
    /// <remarks>
    /// A reason is required by the entity as well as the validator. Waiving money
    /// owed is exactly the operation that has to be reviewable afterwards.
    /// </remarks>
    /// <response code="200">The waived fine.</response>
    /// <response code="404">No fine with this id exists.</response>
    /// <response code="409">Already paid, or already waived.</response>
    /// <response code="422">No reason supplied.</response>
    [HttpPost("{id:int}/waive", Name = "WaiveFine")]
    [ProducesResponseType<FineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FineDto>> Waive(
        int id,
        [FromBody] WaiveFineRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _fineService.WaiveAsync(id, request, cancellationToken));
    }
}
