using Library.Application.Common.Models;
using Library.Application.Members.Dtos;
using Library.Application.Members.Requests;
using Library.Domain.Entities;

namespace Library.Application.Members;

/// <summary>
/// Read and write access to members and membership types.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Books.IBookRepository"/>: reads return materialised DTOs
/// so <c>IQueryable</c> never escapes, writes return domain entities so a use
/// case can invoke the methods that enforce the invariants. Nothing here
/// commits — that is <see cref="Common.Abstractions.IUnitOfWork"/>'s job.
/// </remarks>
public interface IMemberRepository
{
    // ---------------------------------------------------------------- reads

    Task<PagedResult<MemberSummaryDto>> SearchAsync(
        MemberSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<MemberDetailDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Looks a member up by their printed membership number.</summary>
    /// <remarks>
    /// The lookup a librarian actually performs at the desk: the number is on
    /// the card in front of them, the surrogate id is not.
    /// </remarks>
    Task<MemberDetailDto?> GetByMembershipNumberAsync(
        string membershipNumber,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when this email already belongs to a member.
    /// </summary>
    /// <remarks>
    /// <c>excludeMemberId</c> is the member being edited, if any. Without it, an
    /// update that leaves the email unchanged matches itself and reports a false
    /// conflict on every ordinary edit.
    /// </remarks>
    Task<bool> EmailExistsAsync(
        string email,
        int? excludeMemberId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MembershipTypeDto>> GetMembershipTypesAsync(
        CancellationToken cancellationToken = default);

    Task<MembershipTypeDto?> GetMembershipTypeByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<bool> MembershipTypeExistsAsync(
        int membershipTypeId,
        CancellationToken cancellationToken = default);

    // --------------------------------------------------------------- writes

    /// <summary>Loads a tracked member, with membership type, for modification.</summary>
    Task<Member?> GetEntityAsync(int id, CancellationToken cancellationToken = default);

    void Add(Member member);

    void Remove(Member member);

    void AddMembershipType(MembershipType membershipType);

    /// <summary>
    /// The stored spelling of a membership type matching this name, or null.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively, and returns the name as STORED so a conflict
    /// message can quote the row on file rather than echoing the caller back at
    /// themselves.
    /// </remarks>
    Task<string?> FindMembershipTypeNameAsync(
        string name,
        CancellationToken cancellationToken = default);
}
