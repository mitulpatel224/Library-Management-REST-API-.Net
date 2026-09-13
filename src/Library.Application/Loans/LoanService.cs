using Library.Application.Books;
using Library.Application.Common.Abstractions;
using Library.Application.Loans.Dtos;
using Library.Application.Loans.Requests;
using Library.Application.Members;
using Library.Domain.Entities;
using Library.Domain.Exceptions;

namespace Library.Application.Loans;

/// <summary>Lending use cases.</summary>
public interface ILoanService
{
    Task<LoanDetailDto> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<LoanDetailDto> IssueAsync(
        IssueLoanRequest request, CancellationToken cancellationToken = default);
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
    private readonly IClock _clock;

    public LoanService(
        ILoanRepository repository,
        IBookRepository bookRepository,
        IMemberRepository memberRepository,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _repository = repository;
        _bookRepository = bookRepository;
        _memberRepository = memberRepository;
        _unitOfWork = unitOfWork;
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

        return await GetByIdAsync(loan.Id, cancellationToken);
    }
}
