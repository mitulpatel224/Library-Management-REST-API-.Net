using Library.Application.Common.Abstractions;
using Library.Application.Common.Models;
using Library.Application.Members.Dtos;
using Library.Application.Members.Requests;
using Library.Domain.Entities;
using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.Application.Members;

/// <summary>Member use cases.</summary>
public interface IMemberService
{
    Task<PagedResult<MemberSummaryDto>> SearchAsync(
        MemberSearchRequest request, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> GetByMembershipNumberAsync(
        string membershipNumber, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> RegisterAsync(
        CreateMemberRequest request, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> UpdateAsync(
        int id, UpdateMemberRequest request, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> SuspendAsync(
        int id, SuspendMemberRequest request, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> ReactivateAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Marks a membership lapsed through time rather than conduct.</summary>
    Task<MemberDetailDto> ExpireAsync(int id, CancellationToken cancellationToken = default);

    Task<MemberDetailDto> CancelAsync(
        int id, CancelMemberRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MembershipTypeDto>> GetMembershipTypesAsync(
        CancellationToken cancellationToken = default);

    Task<MembershipTypeDto> GetMembershipTypeByIdAsync(
        int id, CancellationToken cancellationToken = default);

    Task<MembershipTypeDto> CreateMembershipTypeAsync(
        CreateMembershipTypeRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMemberService"/>
public sealed class MemberService : IMemberService
{
    private readonly IMemberRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public MemberService(IMemberRepository repository, IUnitOfWork unitOfWork, IClock clock)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<PagedResult<MemberSummaryDto>> SearchAsync(
        MemberSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _repository.SearchAsync(request, cancellationToken);
    }

    public async Task<MemberDetailDto> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
               ?? throw new NotFoundException("Member", id);
    }

    public async Task<MemberDetailDto> GetByMembershipNumberAsync(
        string membershipNumber,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetByMembershipNumberAsync(membershipNumber, cancellationToken)
               ?? throw new NotFoundException("Member", membershipNumber);
    }

    /// <summary>
    /// Registers a member and issues their membership number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two saves, one transaction.</b> The membership number is derived from
    /// the surrogate key, which the database does not assign until the insert
    /// completes — so the row is written, the number computed from the id it
    /// received, and the row updated.
    /// </para>
    /// <para>
    /// Both saves run inside the same <c>DbContext</c> and therefore the same
    /// ambient transaction, so a failure on the second rolls back the first.
    /// There is no window in which a member exists without a number.
    /// </para>
    /// </remarks>
    public async Task<MemberDetailDto> RegisterAsync(
        CreateMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Parsed through the value objects first: both normalise, and the
        // normalised form is what the uniqueness check below must compare.
        Email email = Email.Create(request.Email);

        PhoneNumber? phone = string.IsNullOrWhiteSpace(request.Phone)
            ? null
            : PhoneNumber.Create(request.Phone);

        if (await _repository.EmailExistsAsync(email.Value, cancellationToken: cancellationToken))
        {
            throw new ConflictException(
                "member.duplicate_email",
                $"A member is already registered with the email '{email.Value}'.");
        }

        if (!await _repository.MembershipTypeExistsAsync(request.MembershipTypeId, cancellationToken))
        {
            throw new BusinessRuleViolationException(
                "member.membership_type_not_found",
                $"Membership type {request.MembershipTypeId} does not exist.");
        }

        // The injected clock, not DateOnly.FromDateTime(DateTime.UtcNow) - the
        // join date feeds the membership number's year segment, so a test needs
        // to control it.
        DateOnly joinedOn = request.JoinedOn ?? _clock.Today;

        Member member = Member.Create(
            request.FullName, email, request.MembershipTypeId, joinedOn, phone, request.Address);

        _repository.Add(member);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        member.AssignMembershipNumber();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(member.Id, cancellationToken);
    }

    public async Task<MemberDetailDto> UpdateAsync(
        int id,
        UpdateMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Member member = await _repository.GetEntityAsync(id, cancellationToken)
                        ?? throw new NotFoundException("Member", id);

        Email email = Email.Create(request.Email);

        PhoneNumber? phone = string.IsNullOrWhiteSpace(request.Phone)
            ? null
            : PhoneNumber.Create(request.Phone);

        // Excluding this member is essential: without it, saving a member whose
        // email has not changed matches itself and reports a false conflict on
        // every ordinary edit.
        if (await _repository.EmailExistsAsync(email.Value, id, cancellationToken))
        {
            throw new ConflictException(
                "member.duplicate_email",
                $"Another member is already registered with the email '{email.Value}'.");
        }

        if (!await _repository.MembershipTypeExistsAsync(request.MembershipTypeId, cancellationToken))
        {
            throw new BusinessRuleViolationException(
                "member.membership_type_not_found",
                $"Membership type {request.MembershipTypeId} does not exist.");
        }

        member.UpdateDetails(
            request.FullName, email, phone, request.Address, request.MembershipTypeId);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Status transitions.
    //
    // Each is its own endpoint rather than a status field on the update payload.
    // "Suspend this member, with this reason" is a different operation from
    // "correct this member's phone number", and collapsing them would mean a
    // routine edit could silently change borrowing rights.
    // -----------------------------------------------------------------------

    public async Task<MemberDetailDto> SuspendAsync(
        int id,
        SuspendMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Member member = await _repository.GetEntityAsync(id, cancellationToken)
                        ?? throw new NotFoundException("Member", id);

        // The entity enforces both rules - reason required, cancelled members
        // cannot be suspended - and throws the right exception type for each.
        member.Suspend(request.Reason);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<MemberDetailDto> ExpireAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        Member member = await _repository.GetEntityAsync(id, cancellationToken)
                        ?? throw new NotFoundException("Member", id);

        member.Expire();

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<MemberDetailDto> ReactivateAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        Member member = await _repository.GetEntityAsync(id, cancellationToken)
                        ?? throw new NotFoundException("Member", id);

        member.Reactivate();

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<MemberDetailDto> CancelAsync(
        int id,
        CancelMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Member member = await _repository.GetEntityAsync(id, cancellationToken)
                        ?? throw new NotFoundException("Member", id);

        member.Cancel(request.Reason);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    // -------------------------------------------------------- membership types

    public Task<IReadOnlyList<MembershipTypeDto>> GetMembershipTypesAsync(
        CancellationToken cancellationToken = default)
        => _repository.GetMembershipTypesAsync(cancellationToken);

    public async Task<MembershipTypeDto> GetMembershipTypeByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetMembershipTypeByIdAsync(id, cancellationToken)
               ?? throw new NotFoundException("MembershipType", id);
    }

    public async Task<MembershipTypeDto> CreateMembershipTypeAsync(
        CreateMembershipTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? existingName =
            await _repository.FindMembershipTypeNameAsync(request.Name, cancellationToken);

        if (existingName is not null)
        {
            throw new ConflictException(
                "membership_type.duplicate_name",
                $"A membership type named '{existingName}' already exists.");
        }

        MembershipType type = MembershipType.Create(
            request.Name, request.MaxConcurrentLoans, request.LoanPeriodDays, request.Description);

        _repository.AddMembershipType(type);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new MembershipTypeDto
        {
            Id = type.Id,
            Name = type.Name,
            Description = type.Description,
            MaxConcurrentLoans = type.MaxConcurrentLoans,
            LoanPeriodDays = type.LoanPeriodDays,
            MemberCount = 0,
        };
    }
}
