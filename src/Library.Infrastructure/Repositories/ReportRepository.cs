using System.Globalization;
using System.Runtime.CompilerServices;
using Library.Application.Reports;
using Library.Application.Reports.Dtos;
using Library.Application.Reports.Requests;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Repositories;

/// <summary>EF Core implementation of the reporting reads.</summary>
public sealed class ReportRepository : IReportRepository
{
    private readonly LibraryDbContext _context;

    public ReportRepository(LibraryDbContext context) => _context = context;

    /// <summary>
    /// Streams the catalogue, projecting straight into the export row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AsAsyncEnumerable</c> rather than <c>ToListAsync</c> is what makes this
    /// an export rather than a memory spike: EF Core reads from the open data
    /// reader as the consumer pulls, so the client starts downloading while the
    /// database is still producing rows.
    /// </para>
    /// <para>
    /// The authors and genres are flattened into <c>;</c>-separated strings in
    /// SQL, matching the shape the importer reads — an exported catalogue can be
    /// edited in a spreadsheet and imported straight back.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<BookExportRow> StreamBooksAsync(
        BookReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IQueryable<Book> query = _context.Books.AsNoTracking();

        if (request.CategoryId is > 0)
        {
            query = query.Where(b => b.CategoryId == request.CategoryId);
        }

        if (request.PublisherId is > 0)
        {
            query = query.Where(b => b.PublisherId == request.PublisherId);
        }

        if (request.AvailableOnly)
        {
            query = query.Where(b => b.Copies.Any(c => c.Status == CopyStatus.Available));
        }

        return query
            .OrderBy(b => b.Title)
            .ThenBy(b => b.Id)
            .Select(b => new BookExportRow
            {
                Id = b.Id,
                Isbn = b.Isbn,
                Title = b.Title,
                Subtitle = b.Subtitle,
                CategoryName = b.Category.Name,
                PublisherName = b.Publisher != null ? b.Publisher.Name : null,
                PublishedOn = b.PublishedOn,
                Language = b.Language,
                PageCount = b.PageCount,

                // Counted and joined in SQL. Loading the collections to do this
                // in C# is the N+1 that turns a 200,000-row export into 600,000
                // queries.
                Authors = string.Join(";", b.BookAuthors
                    .OrderBy(ba => ba.AuthorOrder)
                    .Select(ba => ba.Author.FirstName + " " + ba.Author.LastName)),
                Genres = string.Join(";", b.BookGenres.Select(bg => bg.Genre.Name)),
                TotalCopies = b.Copies.Count,
                AvailableCopies = b.Copies.Count(c => c.Status == CopyStatus.Available),
            })
            .AsAsyncEnumerable();
    }

    /// <summary>
    /// Streams loans in a period, with status and overdue days evaluated at
    /// <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status is computed in the projection rather than by calling
    /// <c>Loan.StatusAt</c>, which EF Core cannot translate — a method on an
    /// entity has no SQL equivalent. Expressing the same rule as a conditional
    /// keeps the work in the database.
    /// </para>
    /// <para>
    /// That duplication is a real cost and worth naming: the rule now exists in
    /// two places, and they can drift. <c>LoanTests</c> pins the entity's
    /// behaviour and the report tests pin this one, so a divergence fails a test
    /// rather than quietly producing a wrong report.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<LoanExportRow> StreamLoansAsync(
        LoanReportRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IQueryable<Loan> query = _context.Loans.AsNoTracking();

        if (request.From is not null)
        {
            DateTimeOffset from = ToStartOfDay(request.From.Value);
            query = query.Where(l => l.IssuedAt >= from);
        }

        if (request.To is not null)
        {
            // End of the day, not midnight: a loan issued at 14:00 on the 'to'
            // date belongs in a report that includes that date.
            DateTimeOffset to = ToEndOfDay(request.To.Value);
            query = query.Where(l => l.IssuedAt <= to);
        }

        if (request.MemberId is > 0)
        {
            query = query.Where(l => l.MemberId == request.MemberId);
        }

        query = request.Status switch
        {
            LoanStatus.Returned => query.Where(l => l.ReturnedAt != null),
            LoanStatus.Overdue => query.Where(l => l.ReturnedAt == null && l.DueAt < asOf),
            LoanStatus.Active => query.Where(l => l.ReturnedAt == null && l.DueAt >= asOf),
            _ => query,
        };

        return query
            .OrderByDescending(l => l.IssuedAt)
            .ThenBy(l => l.Id)
            .Select(l => new LoanExportRow
            {
                Id = l.Id,
                Barcode = l.BookCopy.Barcode,
                Isbn = l.BookCopy.Book.Isbn,
                Title = l.BookCopy.Book.Title,
                MembershipNumber = l.Member.MembershipNumber,
                MemberName = l.Member.FullName,
                IssuedAt = l.IssuedAt,
                DueAt = l.DueAt,
                ReturnedAt = l.ReturnedAt,

                Status = l.ReturnedAt != null
                    ? "Returned"
                    : l.DueAt < asOf ? "Overdue" : "Active",

                // The evaluation instant travels with the row; DaysOverdue is
                // derived from it in C#. See LoanExportRow.DaysOverdue for why
                // the day arithmetic is deliberately not done in SQL.
                AsOf = asOf,

                FineAmount = l.Fine != null ? l.Fine.Amount : 0m,
                FineSettled = l.Fine != null && (l.Fine.PaidAt != null || l.Fine.WaivedAt != null),
            })
            .AsAsyncEnumerable();
    }

    /// <summary>
    /// Streams loans still out and past due at <paramref name="asOf"/>, with the
    /// fine each would attract.
    /// </summary>
    /// <remarks>
    /// Carries the member's email and phone, because the point of this report is
    /// to contact them. Anything that forces a second lookup per row defeats it.
    /// </remarks>
    public IAsyncEnumerable<OverdueExportRow> StreamOverdueAsync(
        DateTimeOffset asOf,
        decimal ratePerDay,
        CancellationToken cancellationToken = default)
    {
        return _context.Loans
            .AsNoTracking()
            .Where(l => l.ReturnedAt == null && l.DueAt < asOf)

            // Longest overdue first - that is the order a librarian works the
            // list in, so the report should not need re-sorting.
            .OrderBy(l => l.DueAt)
            .ThenBy(l => l.Id)
            .Select(l => new OverdueExportRow
            {
                LoanId = l.Id,
                Barcode = l.BookCopy.Barcode,
                Title = l.BookCopy.Book.Title,
                MembershipNumber = l.Member.MembershipNumber,
                MemberName = l.Member.FullName,
                MemberEmail = l.Member.Email,
                MemberPhone = l.Member.Phone,
                DueAt = l.DueAt,

                // DaysOverdue and ProjectedFine are derived from these two in
                // C#, because the SQL day-difference function is provider
                // specific and broke this export on SQLite.
                AsOf = asOf,
                RatePerDay = ratePerDay,
            })
            .AsAsyncEnumerable();
    }

    /// <summary>
    /// Aggregates fine totals for a period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every total is a SQL aggregate.</b> The alternative — load the fines and
    /// sum them in C# — returns the same answer and transfers the entire table to
    /// get it. That is the difference between a report that works on seed data and
    /// one that works in production.
    /// </para>
    /// <para>
    /// The per-month breakdown groups on a string built from the year and month.
    /// SQLite has no native date-part function EF can translate for grouping, so
    /// the grouping happens after a projection of just the date and the amounts —
    /// a deliberately small set, not the whole row.
    /// </para>
    /// </remarks>
    public async Task<FineSummaryDto> GetFineSummaryAsync(
        FineSummaryRequest request,
        string currency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset from = ToStartOfDay(request.From!.Value);
        DateTimeOffset to = ToEndOfDay(request.To!.Value);

        IQueryable<Fine> query = _context.Fines
            .AsNoTracking()
            // CreatedAt IS the assessment time: a Fine row comes into existence
            // only when FineAssessmentHandler assesses it, so the audit stamp and
            // the domain event coincide. Worth knowing rather than assuming - if
            // fines ever become creatable ahead of assessment, this filter
            // silently starts answering a different question and the entity needs
            // an explicit AssessedAt.
            .Where(f => f.CreatedAt >= from && f.CreatedAt <= to);

        // One round trip for every headline figure. Splitting these into separate
        // queries would be six round trips for one screen.
        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Assessed = g.Sum(f => f.Amount),
                Paid = g.Sum(f => f.PaidAt != null ? f.Amount : 0m),
                Waived = g.Sum(f => f.WaivedAt != null ? f.Amount : 0m),
                Outstanding = g.Sum(f => f.PaidAt == null && f.WaivedAt == null ? f.Amount : 0m),
                PaidCount = g.Count(f => f.PaidAt != null),
                WaivedCount = g.Count(f => f.WaivedAt != null),
                OutstandingCount = g.Count(f => f.PaidAt == null && f.WaivedAt == null),
                OverdueDays = g.Sum(f => f.DaysOverdue),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Projected to the three columns the grouping needs, so an empty or
        // narrow period does not drag whole Fine rows across.
        var monthly = await query
            .Select(f => new
            {
                f.CreatedAt,
                f.Amount,
                IsOutstanding = f.PaidAt == null && f.WaivedAt == null,
            })
            .ToListAsync(cancellationToken);

        List<FineSummaryByMonthDto> byMonth = [.. monthly
            .GroupBy(f => f.CreatedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FineSummaryByMonthDto
            {
                Month = g.Key,
                Count = g.Count(),
                Assessed = g.Sum(f => f.Amount),
                Outstanding = g.Sum(f => f.IsOutstanding ? f.Amount : 0m),
            })];

        return new FineSummaryDto
        {
            From = request.From.Value,
            To = request.To.Value,
            TotalFines = totals?.Count ?? 0,
            TotalAssessed = totals?.Assessed ?? 0m,
            TotalPaid = totals?.Paid ?? 0m,
            TotalWaived = totals?.Waived ?? 0m,
            TotalOutstanding = totals?.Outstanding ?? 0m,
            PaidCount = totals?.PaidCount ?? 0,
            WaivedCount = totals?.WaivedCount ?? 0,
            OutstandingCount = totals?.OutstandingCount ?? 0,
            TotalOverdueDays = totals?.OverdueDays ?? 0,
            Currency = currency,
            ByMonth = byMonth,
        };
    }

    /// <summary>Midnight UTC on the given date.</summary>
    private static DateTimeOffset ToStartOfDay(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// The last instant of the given date, in UTC.
    /// </summary>
    /// <remarks>
    /// The bound that makes a range inclusive. Using midnight for <c>to</c> would
    /// silently exclude everything that happened on the final day — the
    /// off-by-one that makes a month-end report miss the month's last day.
    /// </remarks>
    private static DateTimeOffset ToEndOfDay(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
}
