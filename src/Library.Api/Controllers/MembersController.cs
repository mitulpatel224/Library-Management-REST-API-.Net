using Library.Application.Common.Models;
using Library.Application.Members;
using Library.Application.Members.Dtos;
using Library.Application.Members.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Library.Api.Controllers;

/// <summary>Member registration and management.</summary>
[ApiController]
[Route("api/members")]
[Produces("application/json")]
public sealed class MembersController : ControllerBase
{
    private readonly IMemberService _memberService;

    public MembersController(IMemberService memberService) => _memberService = memberService;

    /// <summary>Searches members with filtering, sorting and paging.</summary>
    /// <remarks>
    /// <para>
    /// <c>search</c> matches name, membership number and email.
    /// </para>
    /// <para>
    /// <c>sortBy</c> accepts <c>name</c>, <c>email</c>, <c>joined</c>,
    /// <c>status</c>, <c>type</c>, <c>number</c>; anything else falls back to
    /// <c>name</c>. <c>pageSize</c> is capped at 100.
    /// </para>
    /// </remarks>
    /// <response code="200">A page of matching members. An empty page is valid.</response>
    /// <response code="422">
    /// <c>joinedTo</c> is earlier than <c>joinedFrom</c>. Page and sort are
    /// clamped rather than refused; an inverted range has nothing to clamp to.
    /// </response>
    [HttpGet(Name = "SearchMembers")]
    [ProducesResponseType<PagedResult<MemberSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<MemberSummaryDto>>> Search(
        [FromQuery] MemberSearchRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.SearchAsync(request, cancellationToken));
    }

    /// <summary>Gets one member by id.</summary>
    /// <response code="200">The member.</response>
    /// <response code="404">No member with this id exists.</response>
    [HttpGet("{id:int}", Name = "GetMemberById")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberDetailDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.GetByIdAsync(id, cancellationToken));
    }

    /// <summary>Gets one member by their printed membership number.</summary>
    /// <remarks>
    /// The lookup a librarian actually performs: the number is on the card in
    /// front of them, the surrogate id is not. Case-insensitive.
    /// </remarks>
    /// <param name="membershipNumber">e.g. <c>MEM-2026-00042</c>.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The member.</response>
    /// <response code="404">No member with this membership number exists.</response>
    [HttpGet("number/{membershipNumber}", Name = "GetMemberByNumber")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberDetailDto>> GetByNumber(
        string membershipNumber,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.GetByMembershipNumberAsync(
            membershipNumber, cancellationToken));
    }

    /// <summary>Registers a new member and issues their membership number.</summary>
    /// <remarks>
    /// The membership number is assigned by the server, derived from the
    /// database key — it cannot be supplied. Status is always <c>Active</c> on
    /// registration; use the suspend and cancel endpoints to change it.
    /// </remarks>
    /// <response code="201">Created. The <c>Location</c> header points at the member.</response>
    /// <response code="409">A member is already registered with this email.</response>
    /// <response code="422">Validation failed, or the membership type does not exist.</response>
    [HttpPost(Name = "RegisterMember")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MemberDetailDto>> Register(
        [FromBody] CreateMemberRequest request,
        CancellationToken cancellationToken)
    {
        MemberDetailDto created = await _memberService.RegisterAsync(request, cancellationToken);

        return CreatedAtRoute("GetMemberById", new { id = created.Id }, created);
    }

    /// <summary>Updates a member's details.</summary>
    /// <remarks>
    /// Contact details and membership type only. Status is deliberately not
    /// settable here — a routine correction to a phone number must not be able
    /// to restore borrowing rights a librarian withdrew.
    /// </remarks>
    /// <response code="200">The updated member.</response>
    /// <response code="404">No member with this id exists.</response>
    /// <response code="409">Another member already uses this email.</response>
    /// <response code="422">Validation failed, or the membership type does not exist.</response>
    [HttpPut("{id:int}", Name = "UpdateMember")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MemberDetailDto>> Update(
        int id,
        [FromBody] UpdateMemberRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.UpdateAsync(id, request, cancellationToken));
    }

    /// <summary>Withdraws borrowing privileges, recording why.</summary>
    /// <remarks>
    /// A reason is required: a suspension nobody can explain is one nobody can
    /// fairly lift, and the member is entitled to be told.
    /// </remarks>
    /// <response code="200">The suspended member.</response>
    /// <response code="404">No member with this id exists.</response>
    /// <response code="409">The membership is cancelled.</response>
    /// <response code="422">No reason supplied.</response>
    [HttpPost("{id:int}/suspend", Name = "SuspendMember")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MemberDetailDto>> Suspend(
        int id,
        [FromBody] SuspendMemberRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.SuspendAsync(id, request, cancellationToken));
    }

    /// <summary>Restores borrowing privileges.</summary>
    /// <remarks>
    /// Idempotent for an already-active member. Refuses a cancelled membership:
    /// closure is terminal, and a new membership should be registered instead.
    /// </remarks>
    /// <response code="200">The reactivated member.</response>
    /// <response code="404">No member with this id exists.</response>
    /// <response code="409">The membership is cancelled and cannot be reopened.</response>
    [HttpPost("{id:int}/reactivate", Name = "ReactivateMember")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberDetailDto>> Reactivate(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.ReactivateAsync(id, cancellationToken));
    }

    /// <summary>Marks a membership lapsed through time rather than conduct.</summary>
    /// <remarks>
    /// Distinct from suspension, which is a judgement about conduct and is
    /// lifted by appeal. An expired membership is reinstated by renewing, so
    /// <c>reactivate</c> restores it without argument.
    /// </remarks>
    /// <response code="200">The expired member.</response>
    /// <response code="404">No member with this id exists.</response>
    /// <response code="409">The membership is cancelled.</response>
    [HttpPost("{id:int}/expire", Name = "ExpireMember")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberDetailDto>> Expire(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.ExpireAsync(id, cancellationToken));
    }

    /// <summary>Closes a membership.</summary>
    /// <remarks>
    /// <para>
    /// Terminal, and deliberately so — a closed membership that can be quietly
    /// reopened is indistinguishable from one that was never closed, which
    /// matters when the closure was a data protection request. The record is
    /// retained rather than deleted so loan history survives.
    /// </para>
    /// <para>
    /// Cancelling an already-cancelled membership is refused rather than
    /// silently accepted: the closure reason is written once, so a second call
    /// returning 200 would report a revision that did not happen.
    /// </para>
    /// </remarks>
    /// <response code="200">The cancelled member.</response>
    /// <response code="404">No member with this id exists.</response>
    /// <response code="409">The membership is already cancelled. The original closure reason stands.</response>
    [HttpPost("{id:int}/cancel", Name = "CancelMember")]
    [ProducesResponseType<MemberDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MemberDetailDto>> Cancel(
        int id,
        [FromBody] CancelMemberRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.CancelAsync(id, request, cancellationToken));
    }
}

/// <summary>Membership types and the borrowing limits they carry.</summary>
[ApiController]
[Route("api/membership-types")]
[Produces("application/json")]
public sealed class MembershipTypesController : ControllerBase
{
    private readonly IMemberService _memberService;

    public MembershipTypesController(IMemberService memberService) =>
        _memberService = memberService;

    /// <summary>Lists every membership type with its limits and member count.</summary>
    /// <response code="200">The membership types.</response>
    [HttpGet(Name = "GetMembershipTypes")]
    [ProducesResponseType<IReadOnlyList<MembershipTypeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MembershipTypeDto>>> GetAll(
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.GetMembershipTypesAsync(cancellationToken));
    }

    /// <summary>Gets one membership type by id.</summary>
    /// <response code="200">The membership type.</response>
    /// <response code="404">No membership type with this id exists.</response>
    [HttpGet("{id:int}", Name = "GetMembershipTypeById")]
    [ProducesResponseType<MembershipTypeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MembershipTypeDto>> GetById(
        int id,
        CancellationToken cancellationToken)
    {
        return Ok(await _memberService.GetMembershipTypeByIdAsync(id, cancellationToken));
    }

    /// <summary>Creates a membership type.</summary>
    /// <response code="201">The new membership type.</response>
    /// <response code="409">A membership type with this name already exists.</response>
    /// <response code="422">Validation failed.</response>
    [HttpPost(Name = "CreateMembershipType")]
    [ProducesResponseType<MembershipTypeDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MembershipTypeDto>> Create(
        [FromBody] CreateMembershipTypeRequest request,
        CancellationToken cancellationToken)
    {
        MembershipTypeDto created =
            await _memberService.CreateMembershipTypeAsync(request, cancellationToken);

        // Points at the created type, not the collection. A 201 whose Location
        // header returns the whole list makes the client filter for the row it
        // just made - which is the one thing the header exists to save it doing.
        return CreatedAtRoute("GetMembershipTypeById", new { id = created.Id }, created);
    }
}
