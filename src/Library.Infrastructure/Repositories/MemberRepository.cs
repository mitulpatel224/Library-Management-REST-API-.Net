using System.Linq.Expressions;
using Library.Application.Common.Models;
using Library.Application.Members;
using Library.Application.Members.Dtos;
using Library.Application.Members.Requests;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Repositories;

/// <summary>EF Core implementation of the member read and write model.</summary>
public sealed class MemberRepository : IMemberRepository
{
    private readonly LibraryDbContext _context;

    public MemberRepository(LibraryDbContext context) => _context = context;

    public async Task<PagedResult<MemberSummaryDto>> SearchAsync(
        MemberSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        MemberSearchRequest normalized = (MemberSearchRequest)request.Normalize();

        IQueryable<Member> query = _context.Members.AsNoTracking();

        query = ApplyFilters(query, normalized);

        int totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult.Empty<MemberSummaryDto>(normalized.Page, normalized.PageSize);
        }

        query = ApplySorting(query, normalized);

        List<MemberSummaryDto> items = await query
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .Select(member => new MemberSummaryDto
            {
                Id = member.Id,
                MembershipNumber = member.MembershipNumber,
                FullName = member.FullName,
                Email = member.Email,
                Phone = member.Phone,
                MembershipTypeName = member.MembershipType.Name,
                Status = member.Status,
                JoinedOn = member.JoinedOn,

                // Computed in SQL rather than read from Member.CanBorrow, which
                // is a CLR-only property. Keeping the definition in one place is
                // handled by the comment on the entity - if eligibility grows a
                // second condition, both sides change together.
                CanBorrow = member.Status == MemberStatus.Active,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<MemberSummaryDto>(
            items, normalized.Page, normalized.PageSize, totalCount);
    }

    private static IQueryable<Member> ApplyFilters(
        IQueryable<Member> query,
        MemberSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string term = request.Search.Trim();

            // Email is an ordinary mapped string, so LIKE works on it directly.
            // See Member.Email for why it is not a value-object property.
            query = query.Where(m =>
                EF.Functions.Like(m.FullName, $"%{term}%") ||
                EF.Functions.Like(m.MembershipNumber, $"%{term}%") ||
                EF.Functions.Like(m.Email, $"%{term}%"));
        }

        if (request.Status is not null)
        {
            query = query.Where(m => m.Status == request.Status);
        }

        if (request.MembershipTypeId is > 0)
        {
            query = query.Where(m => m.MembershipTypeId == request.MembershipTypeId);
        }

        if (request.JoinedFrom is not null)
        {
            query = query.Where(m => m.JoinedOn >= request.JoinedFrom);
        }

        if (request.JoinedTo is not null)
        {
            query = query.Where(m => m.JoinedOn <= request.JoinedTo);
        }

        if (request.CanBorrowOnly)
        {
            query = query.Where(m => m.Status == MemberStatus.Active);
        }

        return query;
    }

    private static IQueryable<Member> ApplySorting(
        IQueryable<Member> query,
        MemberSearchRequest request)
    {
        Expression<Func<Member, object?>> sortExpression =
            MemberSortOptions.Resolve(request.SortBy);

        IOrderedQueryable<Member> ordered = request.IsDescending
            ? query.OrderByDescending(sortExpression)
            : query.OrderBy(sortExpression);

        // Unique tiebreaker, so paging is stable when rows tie on the sort key.
        return ordered.ThenBy(m => m.Id);
    }

    public async Task<MemberDetailDto?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await ProjectDetail(_context.Members.AsNoTracking().Where(m => m.Id == id))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<MemberDetailDto?> GetByMembershipNumberAsync(
        string membershipNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(membershipNumber))
        {
            return null;
        }

        string normalized = membershipNumber.Trim().ToUpperInvariant();

        return await ProjectDetail(
                _context.Members.AsNoTracking().Where(m => m.MembershipNumber == normalized))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<MemberDetailDto> ProjectDetail(IQueryable<Member> query) =>
        query.Select(member => new MemberDetailDto
        {
            Id = member.Id,
            MembershipNumber = member.MembershipNumber,
            FullName = member.FullName,
            Email = member.Email,
            Phone = member.Phone,
            Address = member.Address,
            MembershipTypeId = member.MembershipTypeId,
            MembershipTypeName = member.MembershipType.Name,

            // Surfaced on the member so a caller planning to issue a loan has
            // the limits without a second request. Phase 4 needs both.
            MaxConcurrentLoans = member.MembershipType.MaxConcurrentLoans,
            LoanPeriodDays = member.MembershipType.LoanPeriodDays,

            Status = member.Status,
            StatusReason = member.StatusReason,
            CanBorrow = member.Status == MemberStatus.Active,
            JoinedOn = member.JoinedOn,
            CreatedAt = member.CreatedAt,
            UpdatedAt = member.UpdatedAt,
        });

    public async Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Members
            .AsNoTracking()
            .AnyAsync(m => m.Id == id, cancellationToken);
    }

    public async Task<bool> EmailExistsAsync(
        string email,
        int? excludeMemberId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        // Lower-cased to match Email's own normalisation. Without this an
        // address differing only in case would pass the check and then fail at
        // the unique index - a 500 where a 409 was correct.
        string normalized = email.Trim().ToLowerInvariant();

        IQueryable<Member> query = _context.Members.AsNoTracking();

        if (excludeMemberId is > 0)
        {
            query = query.Where(m => m.Id != excludeMemberId);
        }

        return await query.AnyAsync(
            m => m.Email == normalized, cancellationToken);
    }

    public async Task<MembershipTypeDto?> GetMembershipTypeByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        return await _context.MembershipTypes
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new MembershipTypeDto
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                MaxConcurrentLoans = t.MaxConcurrentLoans,
                LoanPeriodDays = t.LoanPeriodDays,
                MemberCount = t.Members.Count,
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MembershipTypeDto>> GetMembershipTypesAsync(
        CancellationToken cancellationToken = default)
    {
        return await _context.MembershipTypes
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new MembershipTypeDto
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                MaxConcurrentLoans = t.MaxConcurrentLoans,
                LoanPeriodDays = t.LoanPeriodDays,

                // Counted in SQL. Loading every member to call .Count would be
                // the classic N+1 on a lookup endpoint.
                MemberCount = t.Members.Count,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> MembershipTypeExistsAsync(
        int membershipTypeId,
        CancellationToken cancellationToken = default)
    {
        return await _context.MembershipTypes
            .AsNoTracking()
            .AnyAsync(t => t.Id == membershipTypeId, cancellationToken);
    }

    public async Task<string?> FindMembershipTypeNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Upper-cased on both sides. The unique index behind this check folds
        // case on SQL Server (default CI collation) but not on SQLite (BINARY),
        // so a raw comparison made "Standard" and "standard" two rows on one
        // provider and an index violation on the other - 409 here, 500 there.
        // UPPER() translates on both, so the verdict no longer depends on which
        // provider is configured.
        //
        // SQLite's UPPER() folds ASCII only, so two names differing solely in an
        // accented character would still both be accepted there. Acceptable for
        // a short controlled vocabulary; the alternative is a COLLATE NOCASE on
        // the column, which the migrations would then have to carry per provider.
        string normalized = name.Trim().ToUpperInvariant();

        // CA1862 wants string.Equals(.., StringComparison.OrdinalIgnoreCase) here.
        // That advice is correct for in-memory code and wrong for this line: the
        // lambda is an expression tree EF Core must turn into SQL, and EF cannot
        // translate the StringComparison overloads - taking the suggestion swaps
        // a compile-time warning for a runtime "could not be translated"
        // exception. Scoped to the single statement rather than the file so the
        // rule keeps protecting every ordinary comparison around it.
        //
        // Returns the STORED spelling rather than the caller's: "a membership
        // type named 'standard' already exists" reads badly when the row on file
        // is "Standard", and sends the librarian looking for a type that is not
        // there under that name.
#pragma warning disable CA1862
        return await _context.MembershipTypes
            .AsNoTracking()
            .Where(t => t.Name.ToUpper() == normalized)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken);
#pragma warning restore CA1862
    }

    // --------------------------------------------------------------- writes

    public async Task<Member?> GetEntityAsync(int id, CancellationToken cancellationToken = default)
    {
        // Tracked, and with MembershipType loaded: the status-transition
        // messages quote the membership number, and callers frequently need the
        // loan limits in the same unit of work.
        return await _context.Members
            .Include(m => m.MembershipType)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    public void Add(Member member) => _context.Members.Add(member);

    public void Remove(Member member) => _context.Members.Remove(member);

    public void AddMembershipType(MembershipType membershipType) =>
        _context.MembershipTypes.Add(membershipType);
}
