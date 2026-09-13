using Library.Application.Common.Abstractions;
using Library.Application.Common.Models;
using Library.Application.Loans.Dtos;
using Library.Application.Loans.Requests;
using Library.Application.Members;
using Library.Application.Notifications;
using Library.Domain.Entities;
using Library.Domain.Exceptions;

namespace Library.Application.Loans;

/// <summary>Fine use cases.</summary>
public interface IFineService
{
    Task<FineDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<PagedResult<FineDto>> SearchAsync(
        FineSearchRequest request, CancellationToken cancellationToken = default);

    Task<MemberBalanceDto> GetMemberBalanceAsync(
        int memberId, CancellationToken cancellationToken = default);

    Task<FineDto> PayAsync(
        int id, PayFineRequest request, CancellationToken cancellationToken = default);

    Task<FineDto> WaiveAsync(
        int id, WaiveFineRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IFineService"/>
public sealed class FineService : IFineService
{
    private readonly IFineRepository _repository;
    private readonly IMemberRepository _memberRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationService _notifications;
    private readonly IClock _clock;

    public FineService(
        IFineRepository repository,
        IMemberRepository memberRepository,
        IUnitOfWork unitOfWork,
        INotificationService notifications,
        IClock clock)
    {
        _repository = repository;
        _memberRepository = memberRepository;
        _unitOfWork = unitOfWork;
        _notifications = notifications;
        _clock = clock;
    }

    public async Task<FineDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
               ?? throw new NotFoundException("Fine", id);
    }

    public Task<PagedResult<FineDto>> SearchAsync(
        FineSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _repository.SearchAsync(request, cancellationToken);
    }

    public async Task<MemberBalanceDto> GetMemberBalanceAsync(
        int memberId,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetMemberBalanceAsync(memberId, cancellationToken)
               ?? throw new NotFoundException("Member", memberId);
    }

    /// <summary>Records payment in full.</summary>
    /// <remarks>
    /// Payment is all-or-nothing. Part payment would need an amount-paid column, a
    /// rule for what happens when it exceeds the fine, and a decision about whether
    /// a partly-paid fine still blocks borrowing — none of which is in scope, and
    /// all of which would be half-answered by simply accepting an amount here.
    /// </remarks>
    public async Task<FineDto> PayAsync(
        int id,
        PayFineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Fine fine = await _repository.GetEntityAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Fine", id);

        // The entity refuses a fine that is already paid or waived.
        fine.Pay(_clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _notifications.Notify(new LibraryNotification(
            "fine.paid",
            fine.Member.MembershipNumber,
            $"Fine of {fine.Amount:0.00} paid.",
            _clock.UtcNow));

        return await GetByIdAsync(id, cancellationToken);
    }

    /// <summary>Cancels a fine, recording who asked for it to go and why.</summary>
    public async Task<FineDto> WaiveAsync(
        int id,
        WaiveFineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Fine fine = await _repository.GetEntityAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Fine", id);

        // Reason enforced by the entity as well as the validator: waiving money
        // owed is exactly the operation that has to be reviewable afterwards.
        fine.Waive(_clock.UtcNow, request.Reason);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _notifications.Notify(new LibraryNotification(
            "fine.waived",
            fine.Member.MembershipNumber,
            $"Fine of {fine.Amount:0.00} waived: {request.Reason}",
            _clock.UtcNow));

        return await GetByIdAsync(id, cancellationToken);
    }
}
