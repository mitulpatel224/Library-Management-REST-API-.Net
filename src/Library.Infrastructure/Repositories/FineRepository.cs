using Library.Application.Common.Models;
using Library.Application.Loans;
using Library.Application.Loans.Dtos;
using Library.Application.Loans.Requests;
using Library.Domain.Entities;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Repositories;

/// <inheritdoc cref="IFineRepository"/>
public sealed class FineRepository : IFineRepository
{
    private readonly LibraryDbContext _context;

    public FineRepository(LibraryDbContext context) => _context = context;

    // ---------------------------------------------------------------- reads

    public Task<FineDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return _context.Fines
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(ToDto)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<FineDto>> SearchAsync(
        FineSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PageRequest page = request.Normalize();

        IQueryable<Fine> query = _context.Fines.AsNoTracking();

        if (request.MemberId is > 0)
        {
            query = query.Where(f => f.MemberId == request.MemberId);
        }

        // Settled means paid OR waived. Expressed against the two timestamp
        // columns rather than the entity's IsSettled property, because a computed
        // property has no SQL translation - EF would have to load every row.
        if (request.Outstanding == true)
        {
            query = query.Where(f => f.PaidAt == null && f.WaivedAt == null);
        }
        else if (request.Outstanding == false)
        {
            query = query.Where(f => f.PaidAt != null || f.WaivedAt != null);
        }

        if (request.AssessedFrom.HasValue)
        {
            query = query.Where(f => f.CreatedAt >= request.AssessedFrom.Value);
        }

        if (request.AssessedTo.HasValue)
        {
            query = query.Where(f => f.CreatedAt <= request.AssessedTo.Value);
        }

        int totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult.Empty<FineDto>(page.Page, page.PageSize);
        }

        List<FineDto> items = await query
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(ToDto)
            .ToListAsync(cancellationToken);

        return new PagedResult<FineDto>(items, page.Page, page.PageSize, totalCount);
    }

    public Task<bool> ExistsForLoanAsync(int loanId, CancellationToken cancellationToken = default)
    {
        return _context.Fines
            .AsNoTracking()
            .AnyAsync(f => f.LoanId == loanId, cancellationToken);
    }

    /// <summary>
    /// One round trip for the member's whole lending position.
    /// </summary>
    /// <remarks>
    /// Every figure here is aggregated in SQL. The obvious alternative — load the
    /// member's fines and loans, then use LINQ-to-objects — would pull every row a
    /// long-standing member has ever generated in order to produce five integers.
    /// </remarks>
    public async Task<MemberBalanceDto?> GetMemberBalanceAsync(
        int memberId,
        CancellationToken cancellationToken = default)
    {
        var member = await _context.Members
            .AsNoTracking()
            .Where(m => m.Id == memberId)
            .Select(m => new
            {
                m.Id,
                m.MembershipNumber,
                m.MembershipType.MaxConcurrentLoans,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (member is null)
        {
            return null;
        }

        var fines = await _context.Fines
            .AsNoTracking()
            .Where(f => f.MemberId == memberId && f.PaidAt == null && f.WaivedAt == null)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Sum(f => f.Amount), Count = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);

        int activeLoans = await _context.Loans
            .AsNoTracking()
            .CountAsync(l => l.MemberId == memberId && l.ReturnedAt == null, cancellationToken);

        // The clock is not injected here: "overdue" for this count is measured
        // against the database's own idea of now, and the figure is advisory.
        // Anything that DECIDES on overdue status uses IClock.
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int overdueLoans = await _context.Loans
            .AsNoTracking()
            .CountAsync(
                l => l.MemberId == memberId && l.ReturnedAt == null && l.DueAt < now,
                cancellationToken);

        return new MemberBalanceDto
        {
            MemberId = member.Id,
            MembershipNumber = member.MembershipNumber,
            TotalOutstanding = fines?.Total ?? 0m,
            UnsettledFineCount = fines?.Count ?? 0,
            ActiveLoanCount = activeLoans,
            OverdueLoanCount = overdueLoans,
            MaxConcurrentLoans = member.MaxConcurrentLoans,
            CanBorrowMore = activeLoans < member.MaxConcurrentLoans,
        };
    }

    // --------------------------------------------------------------- writes

    public Task<Fine?> GetEntityAsync(int id, CancellationToken cancellationToken = default)
    {
        return _context.Fines
            .Include(f => f.Member)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    public void Add(Fine fine) => _context.Fines.Add(fine);

    // ---------------------------------------------------------- projections

    private static System.Linq.Expressions.Expression<Func<Fine, FineDto>> ToDto =>
        fine => new FineDto
        {
            Id = fine.Id,
            LoanId = fine.LoanId,
            MemberId = fine.MemberId,
            MembershipNumber = fine.Member.MembershipNumber,
            MemberName = fine.Member.FullName,
            DaysOverdue = fine.DaysOverdue,
            RatePerDay = fine.RatePerDay,
            Amount = fine.Amount,
            OutstandingAmount = fine.PaidAt == null && fine.WaivedAt == null ? fine.Amount : 0m,
            PaidAt = fine.PaidAt,
            WaivedAt = fine.WaivedAt,
            WaivedReason = fine.WaivedReason,
            IsSettled = fine.PaidAt != null || fine.WaivedAt != null,
            CreatedAt = fine.CreatedAt,
        };
}
