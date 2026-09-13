using Library.Application.Common.Abstractions;
using Library.Application.Common.Models;
using Library.Application.Loans;
using Library.Application.Loans.Requests;
using Library.Application.Loans.Dtos;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Repositories;

/// <inheritdoc cref="ILoanRepository"/>
public sealed class LoanRepository : ILoanRepository
{
    private readonly LibraryDbContext _context;
    private readonly IClock _clock;

    public LoanRepository(LibraryDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    // ---------------------------------------------------------------- reads

    public async Task<LoanDetailDto?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        LoanDetailDto? loan = await _context.Loans
            .AsNoTracking()
            .Where(l => l.Id == id)
            .Select(ToDetailDto)
            .FirstOrDefaultAsync(cancellationToken);

        return loan is null ? null : WithComputedStatus(loan, _clock.UtcNow);
    }

    public async Task<PagedResult<LoanSummaryDto>> SearchAsync(
        LoanSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await SearchCoreAsync(_context.Loans.AsNoTracking(), request, cancellationToken);
    }

    public async Task<PagedResult<LoanSummaryDto>> GetForMemberAsync(
        int memberId,
        LoanSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IQueryable<Loan> query = _context.Loans
            .AsNoTracking()
            .Where(l => l.MemberId == memberId);

        return await SearchCoreAsync(query, request, cancellationToken);
    }

    /// <summary>
    /// Filter, count, sort, page, project — in that order, and the order matters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The count runs before paging so it reports how many rows <i>match</i>, not
    /// how many are on this page. Projection comes last so only the columns the
    /// DTO needs cross the wire.
    /// </para>
    /// <para>
    /// <b>Status is filtered in SQL, not in memory.</b> Materialising every loan
    /// to discard the ones that are not overdue would defeat paging entirely — the
    /// page would be drawn from whatever happened to be loaded. The three states
    /// are expressible as date predicates, so they are written that way.
    /// </para>
    /// </remarks>
    private async Task<PagedResult<LoanSummaryDto>> SearchCoreAsync(
        IQueryable<Loan> query,
        LoanSearchRequest request,
        CancellationToken cancellationToken)
    {
        PageRequest page = request.Normalize();
        DateTimeOffset now = _clock.UtcNow;

        query = ApplyFilters(query, request, now);

        int totalCount = await query.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult.Empty<LoanSummaryDto>(page.Page, page.PageSize);
        }

        query = ApplySorting(query, request);

        List<LoanSummaryDto> items = await query
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(ToSummaryDto)
            .ToListAsync(cancellationToken);

        return new PagedResult<LoanSummaryDto>(
            [.. items.Select(item => WithComputedSummaryStatus(item, now))],
            page.Page,
            page.PageSize,
            totalCount);
    }

    private static IQueryable<Loan> ApplyFilters(
        IQueryable<Loan> query,
        LoanSearchRequest request,
        DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string term = request.Search.Trim();

            query = query.Where(l =>
                l.BookCopy.Barcode.Contains(term) ||
                l.BookCopy.Book.Title.Contains(term) ||
                l.Member.FullName.Contains(term) ||
                l.Member.MembershipNumber.Contains(term));
        }

        if (request.MemberId is > 0)
        {
            query = query.Where(l => l.MemberId == request.MemberId);
        }

        if (request.BookCopyId is > 0)
        {
            query = query.Where(l => l.BookCopyId == request.BookCopyId);
        }

        if (request.BookId is > 0)
        {
            query = query.Where(l => l.BookCopy.BookId == request.BookId);
        }

        // The three statuses as date predicates. Returned is "has a return
        // timestamp"; the other two split the open loans on the due date.
        query = request.Status switch
        {
            LoanStatus.Returned => query.Where(l => l.ReturnedAt != null),
            LoanStatus.Overdue => query.Where(l => l.ReturnedAt == null && l.DueAt < now),
            LoanStatus.Active => query.Where(l => l.ReturnedAt == null && l.DueAt >= now),
            _ => query,
        };

        if (request.OverdueOnly)
        {
            query = query.Where(l => l.ReturnedAt == null && l.DueAt < now);
        }

        if (request.IssuedFrom.HasValue)
        {
            query = query.Where(l => l.IssuedAt >= request.IssuedFrom.Value);
        }

        if (request.IssuedTo.HasValue)
        {
            query = query.Where(l => l.IssuedAt <= request.IssuedTo.Value);
        }

        if (request.DueFrom.HasValue)
        {
            query = query.Where(l => l.DueAt >= request.DueFrom.Value);
        }

        if (request.DueTo.HasValue)
        {
            query = query.Where(l => l.DueAt <= request.DueTo.Value);
        }

        return query;
    }

    /// <remarks>
    /// The tiebreaker is not optional. Due dates collide constantly — every copy
    /// issued on the same day to the same membership type shares one — and without
    /// a unique final sort key the database may order ties differently between
    /// queries, so a row can appear on both page 1 and page 2, or on neither.
    /// </remarks>
    private static IQueryable<Loan> ApplySorting(IQueryable<Loan> query, LoanSearchRequest request)
    {
        var expression = LoanSortOptions.Resolve(request.SortBy);

        return request.IsDescending
            ? query.OrderByDescending(expression).ThenByDescending(l => l.Id)
            : query.OrderBy(expression).ThenBy(l => l.Id);
    }

    public async Task<LoanDetailDto?> GetActiveLoanForCopyAsync(
        int bookCopyId,
        CancellationToken cancellationToken = default)
    {
        // "ReturnedAt is null" is the same predicate the filtered unique index
        // uses, so this read is answered by that index rather than a table scan.
        LoanDetailDto? loan = await _context.Loans
            .AsNoTracking()
            .Where(l => l.BookCopyId == bookCopyId && l.ReturnedAt == null)
            .Select(ToDetailDto)
            .FirstOrDefaultAsync(cancellationToken);

        return loan is null ? null : WithComputedStatus(loan, _clock.UtcNow);
    }

    public Task<int> CountActiveLoansForMemberAsync(
        int memberId,
        CancellationToken cancellationToken = default)
    {
        // Counted in SQL. Materialising the member's loans to call .Count would
        // load every row to learn a single number.
        return _context.Loans
            .AsNoTracking()
            .CountAsync(l => l.MemberId == memberId && l.ReturnedAt == null, cancellationToken);
    }

    // --------------------------------------------------------------- writes

    public Task<Loan?> GetEntityAsync(int id, CancellationToken cancellationToken = default)
    {
        // Tracked, with the copy loaded: Loan.Return() calls BookCopy.MarkReturned(),
        // so the copy has to be attached for that change to be saved.
        //
        // MembershipType is included through Member because a renewal with no
        // explicit period defaults to the member's own loan period. Without the
        // ThenInclude that navigation is null, and the default path threw a
        // NullReferenceException - surfacing as a 500 on what should have been a
        // 409 for renewing a closed loan, because the null deref happened before
        // the entity could refuse.
        return _context.Loans
            .Include(l => l.BookCopy)
            .Include(l => l.Member)
                .ThenInclude(m => m.MembershipType)
            .Include(l => l.Fine)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public void Add(Loan loan) => _context.Loans.Add(loan);

    // ---------------------------------------------------------- projections

    /// <summary>
    /// The shared projection. Note what it does <b>not</b> set: <c>Status</c>,
    /// <c>DaysOverdue</c> and <c>DaysRemaining</c>.
    /// </summary>
    /// <remarks>
    /// Those three are functions of "now", and computing them in SQL would mean
    /// either a provider-specific date function (<c>DATEDIFF</c> exists on SQL
    /// Server and not on SQLite) or pushing the clock into the query, which would
    /// make the result depend on the database server's time zone rather than the
    /// injected <c>IClock</c> — and so untestable. They are filled in by
    /// <see cref="WithComputedStatus"/> once the rows are in memory.
    /// </remarks>
    private static System.Linq.Expressions.Expression<Func<Loan, LoanDetailDto>> ToDetailDto =>
        loan => new LoanDetailDto
        {
            Id = loan.Id,
            BookCopyId = loan.BookCopyId,
            Barcode = loan.BookCopy.Barcode,
            BookId = loan.BookCopy.BookId,
            BookTitle = loan.BookCopy.Book.Title,
            Isbn = loan.BookCopy.Book.Isbn,
            MemberId = loan.MemberId,
            MembershipNumber = loan.Member.MembershipNumber,
            MemberName = loan.Member.FullName,
            IssuedAt = loan.IssuedAt,
            DueAt = loan.DueAt,
            ReturnedAt = loan.ReturnedAt,
            CreatedAt = loan.CreatedAt,
            UpdatedAt = loan.UpdatedAt,
            Fine = loan.Fine == null
                ? null
                : new FineDto
                {
                    Id = loan.Fine.Id,
                    LoanId = loan.Fine.LoanId,
                    MemberId = loan.Fine.MemberId,
                    MembershipNumber = loan.Member.MembershipNumber,
                    MemberName = loan.Member.FullName,
                    DaysOverdue = loan.Fine.DaysOverdue,
                    RatePerDay = loan.Fine.RatePerDay,
                    Amount = loan.Fine.Amount,
                    OutstandingAmount = loan.Fine.PaidAt == null && loan.Fine.WaivedAt == null
                        ? loan.Fine.Amount
                        : 0m,
                    PaidAt = loan.Fine.PaidAt,
                    WaivedAt = loan.Fine.WaivedAt,
                    WaivedReason = loan.Fine.WaivedReason,
                    IsSettled = loan.Fine.PaidAt != null || loan.Fine.WaivedAt != null,
                    CreatedAt = loan.Fine.CreatedAt,
                },
        };

    private static System.Linq.Expressions.Expression<Func<Loan, LoanSummaryDto>> ToSummaryDto =>
        loan => new LoanSummaryDto
        {
            Id = loan.Id,
            BookCopyId = loan.BookCopyId,
            Barcode = loan.BookCopy.Barcode,
            BookTitle = loan.BookCopy.Book.Title,
            MemberId = loan.MemberId,
            MembershipNumber = loan.Member.MembershipNumber,
            MemberName = loan.Member.FullName,
            IssuedAt = loan.IssuedAt,
            DueAt = loan.DueAt,
            ReturnedAt = loan.ReturnedAt,
            FineAmount = loan.Fine == null ? null : loan.Fine.Amount,
            FineSettled = loan.Fine != null
                          && (loan.Fine.PaidAt != null || loan.Fine.WaivedAt != null),
        };

    private static LoanSummaryDto WithComputedSummaryStatus(
        LoanSummaryDto loan,
        DateTimeOffset now)
    {
        DateTimeOffset measuredAt = loan.ReturnedAt ?? now;

        return loan with
        {
            Status = loan.ReturnedAt.HasValue
                ? LoanStatus.Returned
                : now > loan.DueAt ? LoanStatus.Overdue : LoanStatus.Active,

            DaysOverdue = measuredAt > loan.DueAt
                ? (int)(measuredAt - loan.DueAt).TotalDays
                : 0,
        };
    }

    /// <summary>
    /// Fills in the three time-relative fields, using the same rules as
    /// <see cref="Loan.StatusAt"/> and <see cref="Loan.DaysOverdueAt"/>.
    /// </summary>
    /// <remarks>
    /// The duplication with the entity is deliberate and narrow: the entity is the
    /// authority for a tracked instance, and this is the read path, which never
    /// materialises entities. Keeping the read path on DTOs is what stops
    /// <c>IQueryable</c> and EF types reaching the controllers.
    /// </remarks>
    private static LoanDetailDto WithComputedStatus(LoanDetailDto loan, DateTimeOffset now)
    {
        DateTimeOffset measuredAt = loan.ReturnedAt ?? now;

        int daysOverdue = measuredAt > loan.DueAt
            ? (int)(measuredAt - loan.DueAt).TotalDays
            : 0;

        LoanStatus status = loan.ReturnedAt.HasValue
            ? LoanStatus.Returned
            : now > loan.DueAt ? LoanStatus.Overdue : LoanStatus.Active;

        // Ceiling, not truncation. A loan issued moments ago for 14 days has
        // 13.999 days left, and truncating reports 13 - so a member is told they
        // have a day less than they were promised at the desk. Rounding up says
        // "you have 14 days", which is what the librarian told them. Overdue days
        // truncate for the mirrored reason: a copy an hour late is not yet a day
        // late, and must not be charged as one.
        return loan with
        {
            Status = status,
            DaysOverdue = daysOverdue,
            DaysRemaining = (int)Math.Ceiling((loan.DueAt - now).TotalDays),
        };
    }
}
