using Library.Application.Common.Abstractions;
using Library.Application.Loans;
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
        return _context.Loans
            .Include(l => l.BookCopy)
            .Include(l => l.Member)
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
