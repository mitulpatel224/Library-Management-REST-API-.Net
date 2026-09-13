using Library.Application.Books;
using Library.Application.Common.Abstractions;
using Library.Application.Common.Models;
using Library.Application.Loans.Dtos;
using Library.Application.Loans.Requests;
using Library.Application.Members;
using Library.Application.Notifications;
using Library.Domain.Entities;
using Library.Domain.Exceptions;

namespace Library.Application.Loans;

/// <summary>Lending use cases.</summary>
public interface ILoanService
{
    Task<LoanDetailDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<PagedResult<LoanSummaryDto>> SearchAsync(
        LoanSearchRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<LoanSummaryDto>> GetForMemberAsync(
        int memberId, LoanSearchRequest request, CancellationToken cancellationToken = default);

    Task<LoanDetailDto> IssueAsync(
        IssueLoanRequest request, CancellationToken cancellationToken = default);

    Task<LoanDetailDto> ReturnAsync(
        int id, ReturnLoanRequest request, CancellationToken cancellationToken = default);

    Task<LoanDetailDto> RenewAsync(
        int id, RenewLoanRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILoanService"/>
public sealed class LoanService : ILoanService
{
    /// <summary>
    /// The generic code <c>UnitOfWork</c> raises for any unique-index violation.
    /// </summary>
    /// <remarks>
    /// <c>UnitOfWork</c> deliberately does not try to work out <i>which</i> index
    /// was violated — doing so would mean parsing the constraint name out of a
    /// provider-specific, localised message, which is the fragility it was written
    /// to avoid. The use case knows which index its insert can hit, so the
    /// translation to a specific code belongs here.
    /// </remarks>
    private const string GenericDuplicateCode = "resource.duplicate";

    private readonly ILoanRepository _repository;
    private readonly IBookRepository _bookRepository;
    private readonly IMemberRepository _memberRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly IClock _clock;

    public LoanService(
        ILoanRepository repository,
        IBookRepository bookRepository,
        IMemberRepository memberRepository,
        IUnitOfWork unitOfWork,
        IDomainEventDispatcher dispatcher,
        INotificationService notifications,
        IClock clock)
    {
        _repository = repository;
        _bookRepository = bookRepository;
        _memberRepository = memberRepository;
        _unitOfWork = unitOfWork;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _clock = clock;
    }

    public async Task<LoanDetailDto> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync(id, cancellationToken)
               ?? throw new NotFoundException("Loan", id);
    }

    /// <summary>
    /// Issues a copy to a member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three checks, one guarantee.</b> The pre-checks below produce readable
    /// errors — "that copy is out with MEM-2026-00042 until Friday" is worth far
    /// more at a desk than a constraint violation. But every one of them reads
    /// before the insert writes, so two concurrent requests for the same copy can
    /// pass all three.
    /// </para>
    /// <para>
    /// The filtered unique index <c>Loan(BookCopyId) WHERE ReturnedAt IS NULL</c>
    /// is what actually settles that race, because it is the only participant that
    /// serialises the writes. The loser's <c>DbUpdateException</c> arrives here as
    /// a <see cref="ConflictException"/> and is re-labelled with the code the
    /// caller expects — so a lost race and a detected conflict are indistinguishable
    /// to the client, which is exactly right: both mean "someone else has it".
    /// </para>
    /// </remarks>
    public async Task<LoanDetailDto> IssueAsync(
        IssueLoanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        BookCopy copy = await _bookRepository.GetCopyEntityAsync(
                            request.BookCopyId, cancellationToken)
                        ?? throw new NotFoundException("Copy", request.BookCopyId);

        Member member = await _memberRepository.GetEntityAsync(
                            request.MemberId, cancellationToken)
                        ?? throw new NotFoundException("Member", request.MemberId);

        // Checked before Loan.Issue so the caller is told which loan holds the
        // copy, rather than only that the copy is unavailable.
        LoanDetailDto? existing = await _repository.GetActiveLoanForCopyAsync(
            request.BookCopyId, cancellationToken);

        if (existing is not null)
        {
            throw new ConflictException(
                "loan.copy_already_on_loan",
                $"Copy '{existing.Barcode}' is already on loan to "
                + $"{existing.MembershipNumber} until {existing.DueAt:yyyy-MM-dd}.");
        }

        int activeLoans = await _repository.CountActiveLoansForMemberAsync(
            request.MemberId, cancellationToken);

        // The limit comes from the member's own membership type, loaded alongside
        // the member by GetEntityAsync - a Student may hold 8 where a Standard
        // member may hold 5.
        int limit = member.MembershipType.MaxConcurrentLoans;

        if (activeLoans >= limit)
        {
            throw new BusinessRuleViolationException(
                "member.loan_limit_reached",
                $"Member '{member.MembershipNumber}' already has {activeLoans} "
                + $"{(activeLoans == 1 ? "copy" : "copies")} out and may hold at most {limit}.");
        }

        // Loan.Issue enforces CanBorrow and marks the copy OnLoan. The due date
        // derives from the membership type's loan period and the injected clock,
        // never from the request - see IssueLoanRequest for why.
        Loan loan = Loan.Issue(
            copy,
            member,
            _clock.UtcNow,
            member.MembershipType.LoanPeriodDays);

        _repository.Add(loan);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException ex) when (ex.ErrorCode == GenericDuplicateCode)
        {
            // Lost the race against a concurrent issue of the same copy. The only
            // unique index this insert can violate is the active-loan one.
            throw new ConflictException(
                "loan.copy_already_on_loan",
                $"Copy '{copy.Barcode}' was issued to another member a moment ago.",
                ex);
        }

        // Nothing is raised on issue today. DispatchAsync is still called so that
        // adding an event to Loan.Issue later needs no change here - and so the
        // ordering rule (commit, THEN dispatch) is established at every write
        // rather than only where it currently matters.
        await _dispatcher.DispatchAsync(cancellationToken);

        _notifications.Notify(new LibraryNotification(
            "loan.issued",
            member.MembershipNumber,
            $"'{copy.Barcode}' issued, due {loan.DueAt:yyyy-MM-dd}.",
            _clock.UtcNow));

        return await GetByIdAsync(loan.Id, cancellationToken);
    }

    public Task<PagedResult<LoanSummaryDto>> SearchAsync(
        LoanSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _repository.SearchAsync(request, cancellationToken);
    }

    public async Task<PagedResult<LoanSummaryDto>> GetForMemberAsync(
        int memberId,
        LoanSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked so an unknown member is a 404 rather than an empty page. "This
        // member has no loans" and "there is no such member" are different answers
        // and a caller has to be able to tell them apart.
        if (!await _memberRepository.ExistsAsync(memberId, cancellationToken))
        {
            throw new NotFoundException("Member", memberId);
        }

        return await _repository.GetForMemberAsync(memberId, request, cancellationToken);
    }

    /// <summary>
    /// Takes a copy back, and lets the fine be assessed after the fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ordering here is the phase's central lesson.</b> <c>Loan.Return</c>
    /// only <i>collects</i> a <see cref="Domain.Events.LoanReturnedEvent"/>.
    /// Nothing is dispatched until <c>SaveChangesAsync</c> has succeeded, so a
    /// rollback cannot leave a member fined for a return that never happened.
    /// </para>
    /// <para>
    /// The notification is raised through the C# <c>event</c> instead, and
    /// deliberately: it is advisory, writes nothing, and nobody is harmed if a
    /// subscriber misses it. A fine is a financial record, so it gets the
    /// mechanism with transactional ordering. Same building, different guarantees.
    /// </para>
    /// </remarks>
    public async Task<LoanDetailDto> ReturnAsync(
        int id,
        ReturnLoanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Loan loan = await _repository.GetEntityAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Loan", id);

        // The entity refuses a second return and a return before the issue date,
        // and raises the domain event. The clock is passed in, never read there.
        loan.Return(_clock.UtcNow, request.Condition);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // AFTER the commit. FineAssessmentHandler writes the fine in its own
        // transaction; if it fails, the return still stands and the fine is
        // recoverable from the log. The reverse - a rolled-back return with a
        // fine already written - would not be.
        await _dispatcher.DispatchAsync(cancellationToken);

        int daysOverdue = loan.DaysOverdueAt(_clock.UtcNow);

        _notifications.Notify(new LibraryNotification(
            daysOverdue > 0 ? "loan.returned_late" : "loan.returned",
            loan.Member.MembershipNumber,
            daysOverdue > 0
                ? $"'{loan.BookCopy.Barcode}' returned {daysOverdue} day(s) late."
                : $"'{loan.BookCopy.Barcode}' returned on time.",
            _clock.UtcNow));

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<LoanDetailDto> RenewAsync(
        int id,
        RenewLoanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Loan loan = await _repository.GetEntityAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Loan", id);

        // Defaults to the member's own loan period, which is what "renew" means at
        // a desk - a Student gets another 28 days, not another 14.
        int additionalDays = request.AdditionalDays
                             ?? loan.Member.MembershipType.LoanPeriodDays;

        // The entity refuses a closed loan and an overdue one. Renewing an overdue
        // loan would erase a fine that has already accrued.
        loan.Renew(_clock.UtcNow, additionalDays);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _dispatcher.DispatchAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }
}
