using Library.Application.Loans.Dtos;
using Library.Domain.Entities;

namespace Library.Application.Loans;

/// <summary>
/// Read and write access to loans and fines.
/// </summary>
/// <remarks>
/// Mirrors the book and member repositories: reads return materialised DTOs so
/// <c>IQueryable</c> never escapes, writes return domain entities so a use case can
/// invoke the methods that enforce the invariants. Nothing here commits — that is
/// <see cref="Common.Abstractions.IUnitOfWork"/>'s job.
/// </remarks>
public interface ILoanRepository
{
    // ---------------------------------------------------------------- reads

    Task<LoanDetailDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The open loan for this copy, if any.
    /// </summary>
    /// <remarks>
    /// Used to produce a readable 409 before attempting the insert. It is
    /// <b>not</b> what makes the rule safe — this reads, and another request can
    /// write between the read and our insert. The filtered unique index settles
    /// that; this only improves the error message.
    /// </remarks>
    Task<LoanDetailDto?> GetActiveLoanForCopyAsync(
        int bookCopyId,
        CancellationToken cancellationToken = default);

    /// <summary>How many copies this member currently has out.</summary>
    Task<int> CountActiveLoansForMemberAsync(
        int memberId,
        CancellationToken cancellationToken = default);

    // --------------------------------------------------------------- writes

    /// <summary>Loads a tracked loan, with copy and member, for modification.</summary>
    Task<Loan?> GetEntityAsync(int id, CancellationToken cancellationToken = default);

    void Add(Loan loan);
}
