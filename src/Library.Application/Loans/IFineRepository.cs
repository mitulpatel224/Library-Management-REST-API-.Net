using Library.Application.Common.Models;
using Library.Application.Loans.Dtos;
using Library.Application.Loans.Requests;
using Library.Domain.Entities;

namespace Library.Application.Loans;

/// <summary>Read and write access to fines.</summary>
public interface IFineRepository
{
    // ---------------------------------------------------------------- reads

    Task<FineDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<PagedResult<FineDto>> SearchAsync(
        FineSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when a fine has already been assessed for this loan.
    /// </summary>
    /// <remarks>
    /// Guards against a replayed domain event. It is not the real protection —
    /// the unique index on <c>Fine.LoanId</c> is — but it turns a double dispatch
    /// into a quiet no-op rather than a logged constraint violation.
    /// </remarks>
    Task<bool> ExistsForLoanAsync(int loanId, CancellationToken cancellationToken = default);

    /// <summary>What this member currently owes, plus their borrowing position.</summary>
    Task<MemberBalanceDto?> GetMemberBalanceAsync(
        int memberId,
        CancellationToken cancellationToken = default);

    // --------------------------------------------------------------- writes

    Task<Fine?> GetEntityAsync(int id, CancellationToken cancellationToken = default);

    void Add(Fine fine);
}
