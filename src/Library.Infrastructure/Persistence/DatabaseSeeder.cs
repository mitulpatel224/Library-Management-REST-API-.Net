using Library.Application.Common.Abstractions;
using Library.Domain.Entities;
using Library.Domain.Enums;
using Library.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Library.Infrastructure.Persistence;

/// <summary>
/// Populates an empty database with a realistic catalogue for development and demos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seeder class and not <c>HasData</c> in the model configuration.</b>
/// EF Core's <c>HasData</c> bakes seed rows into the migration itself, which
/// means every change to the sample data is a schema migration, and the rows
/// must have hard-coded primary keys. That is right for genuine reference data
/// (a currency table), and wrong for demo content.
/// </para>
/// <para>
/// Running it through the domain factory methods has a second benefit: the seed
/// data goes through the same validation as real input, so an invalid ISBN in
/// the sample set fails loudly here instead of quietly populating a broken row.
/// </para>
/// <para>
/// The whole thing is a no-op when any book already exists, so it is safe to
/// call on every startup.
/// </para>
/// </remarks>
public sealed partial class DatabaseSeeder
{
    private readonly LibraryDbContext _context;
    private readonly IClock _clock;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(LibraryDbContext context, IClock clock, ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _clock = clock;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await _context.Books.AnyAsync(cancellationToken))
        {
            LogAlreadySeeded();
            return;
        }

        LogSeedingStarted();

        // ---------------------------------------------------------------
        // Categories. Two roots with children, to exercise the self-reference.
        // ---------------------------------------------------------------
        Category fiction = Category.Create("Fiction", "Novels and short stories");
        Category nonFiction = Category.Create("Non-Fiction", "Factual works");
        Category technology = Category.Create("Technology", "Computing and engineering");
        _context.Categories.AddRange(fiction, nonFiction, technology);
        await _context.SaveChangesAsync(cancellationToken);

        Category scienceFiction = Category.Create("Science Fiction", "Speculative fiction", fiction.Id);
        Category softwareEngineering = Category.Create(
            "Software Engineering", "Programming practice", technology.Id);
        _context.Categories.AddRange(scienceFiction, softwareEngineering);

        // ---------------------------------------------------------------
        // Publishers
        // ---------------------------------------------------------------
        Publisher addisonWesley = Publisher.Create("Addison-Wesley", "United States");
        Publisher oreilly = Publisher.Create("O'Reilly Media", "United States");
        Publisher penguin = Publisher.Create("Penguin Books", "United Kingdom");
        Publisher prenticeHall = Publisher.Create("Prentice Hall", "United States");
        _context.Publishers.AddRange(addisonWesley, oreilly, penguin, prenticeHall);

        // ---------------------------------------------------------------
        // Genres
        // ---------------------------------------------------------------
        Genre programming = Genre.Create("Programming");
        Genre softwareDesign = Genre.Create("Software Design");
        Genre dystopian = Genre.Create("Dystopian");
        Genre classic = Genre.Create("Classic Literature");
        Genre reference = Genre.Create("Reference");
        Genre popularScience = Genre.Create("Popular Science");
        _context.Genres.AddRange(
            programming, softwareDesign, dystopian, classic, reference, popularScience);

        // ---------------------------------------------------------------
        // Authors. Multi-author titles below prove the M:N relationship and the
        // AuthorOrder column actually work.
        // ---------------------------------------------------------------
        Author gamma = Author.Create("Erich", "Gamma");
        Author helm = Author.Create("Richard", "Helm");
        Author johnson = Author.Create("Ralph", "Johnson");
        Author vlissides = Author.Create("John", "Vlissides");
        Author martin = Author.Create("Robert C.", "Martin");
        Author fowler = Author.Create("Martin", "Fowler");
        Author evans = Author.Create("Eric", "Evans");
        Author orwell = Author.Create("George", "Orwell", new DateOnly(1903, 6, 25));
        Author huxley = Author.Create("Aldous", "Huxley", new DateOnly(1894, 7, 26));
        Author austen = Author.Create("Jane", "Austen", new DateOnly(1775, 12, 16));
        Author knuth = Author.Create("Donald", "Knuth");
        Author sagan = Author.Create("Carl", "Sagan", new DateOnly(1934, 11, 9));

        _context.Authors.AddRange(
            gamma, helm, johnson, vlissides, martin, fowler,
            evans, orwell, huxley, austen, knuth, sagan);

        await _context.SaveChangesAsync(cancellationToken);

        // ---------------------------------------------------------------
        // Books.
        //
        // Every ISBN below is a real, checksum-valid ISBN-13. That matters: the
        // Isbn value object validates the check digit, so a made-up number would
        // throw here rather than silently seeding bad data.
        // ---------------------------------------------------------------
        var seedBooks = new List<(Book Book, int[] AuthorIds, int[] GenreIds, int CopyCount)>
        {
            (Book.Create(
                Isbn.Create("9780201633610"),
                "Design Patterns",
                softwareEngineering.Id,
                subtitle: "Elements of Reusable Object-Oriented Software",
                publisherId: addisonWesley.Id,
                publishedOn: new DateOnly(1994, 10, 31),
                language: "en",
                pageCount: 395,
                description: "Catalogue of 23 classic object-oriented design patterns."),
                [gamma.Id, helm.Id, johnson.Id, vlissides.Id],
                [programming.Id, softwareDesign.Id],
                3),

            (Book.Create(
                Isbn.Create("9780132350884"),
                "Clean Code",
                softwareEngineering.Id,
                subtitle: "A Handbook of Agile Software Craftsmanship",
                publisherId: prenticeHall.Id,
                publishedOn: new DateOnly(2008, 8, 1),
                language: "en",
                pageCount: 464,
                description: "Principles and practices for writing readable, maintainable code."),
                [martin.Id],
                [programming.Id],
                4),

            (Book.Create(
                Isbn.Create("9780134757599"),
                "Refactoring",
                softwareEngineering.Id,
                subtitle: "Improving the Design of Existing Code",
                publisherId: addisonWesley.Id,
                publishedOn: new DateOnly(2018, 11, 20),
                language: "en",
                pageCount: 448,
                description: "A catalogue of behaviour-preserving code transformations."),
                [fowler.Id],
                [programming.Id, softwareDesign.Id],
                2),

            (Book.Create(
                Isbn.Create("9780321125217"),
                "Domain-Driven Design",
                softwareEngineering.Id,
                subtitle: "Tackling Complexity in the Heart of Software",
                publisherId: addisonWesley.Id,
                publishedOn: new DateOnly(2003, 8, 20),
                language: "en",
                pageCount: 560,
                description: "Modelling complex domains through a shared ubiquitous language."),
                [evans.Id],
                [softwareDesign.Id],
                2),

            (Book.Create(
                Isbn.Create("9780134685991"),
                "Effective Java",
                technology.Id,
                publisherId: addisonWesley.Id,
                publishedOn: new DateOnly(2017, 12, 27),
                language: "en",
                pageCount: 416,
                description: "Best practices for the Java platform."),
                [],
                [programming.Id, reference.Id],
                2),

            (Book.Create(
                Isbn.Create("9780201896831"),
                "The Art of Computer Programming, Volume 1",
                technology.Id,
                subtitle: "Fundamental Algorithms",
                publisherId: addisonWesley.Id,
                publishedOn: new DateOnly(1997, 7, 17),
                language: "en",
                pageCount: 650,
                description: "The foundational treatise on algorithms and data structures."),
                [knuth.Id],
                [programming.Id, reference.Id],
                1),

            (Book.Create(
                Isbn.Create("9780451524935"),
                "Nineteen Eighty-Four",
                scienceFiction.Id,
                publisherId: penguin.Id,
                publishedOn: new DateOnly(1949, 6, 8),
                language: "en",
                pageCount: 328,
                description: "A totalitarian future under constant surveillance."),
                [orwell.Id],
                [dystopian.Id, classic.Id],
                5),

            (Book.Create(
                Isbn.Create("9780060850524"),
                "Brave New World",
                scienceFiction.Id,
                publisherId: penguin.Id,
                publishedOn: new DateOnly(1932, 1, 1),
                language: "en",
                pageCount: 288,
                description: "A society engineered for stability and pleasure."),
                [huxley.Id],
                [dystopian.Id, classic.Id],
                3),

            (Book.Create(
                Isbn.Create("9780141439518"),
                "Pride and Prejudice",
                fiction.Id,
                publisherId: penguin.Id,
                publishedOn: new DateOnly(1813, 1, 28),
                language: "en",
                pageCount: 432,
                description: "Manners, marriage and misjudgement in Regency England."),
                [austen.Id],
                [classic.Id],
                4),

            (Book.Create(
                Isbn.Create("9780345539434"),
                "Cosmos",
                nonFiction.Id,
                publisherId: penguin.Id,
                publishedOn: new DateOnly(1980, 1, 1),
                language: "en",
                pageCount: 396,
                description: "The universe, and our place in it."),
                [sagan.Id],
                [popularScience.Id],
                2),

            (Book.Create(
                Isbn.Create("9781449373320"),
                "Designing Data-Intensive Applications",
                softwareEngineering.Id,
                publisherId: oreilly.Id,
                publishedOn: new DateOnly(2017, 3, 16),
                language: "en",
                pageCount: 616,
                description: "The ideas behind reliable, scalable, maintainable systems."),
                [],
                [programming.Id, softwareDesign.Id],
                3),

            (Book.Create(
                Isbn.Create("9780596007126"),
                "Head First Design Patterns",
                softwareEngineering.Id,
                publisherId: oreilly.Id,
                publishedOn: new DateOnly(2004, 10, 25),
                language: "en",
                pageCount: 694,
                description: "A visual introduction to design patterns."),
                [],
                [programming.Id, softwareDesign.Id],
                2),

            (Book.Create(
                Isbn.Create("9780135957059"),
                "The Pragmatic Programmer",
                softwareEngineering.Id,
                subtitle: "Your Journey to Mastery",
                publisherId: addisonWesley.Id,
                publishedOn: new DateOnly(2019, 9, 13),
                language: "en",
                pageCount: 352,
                description: "Practical advice on the craft of software development."),
                [],
                [programming.Id],
                3),

            (Book.Create(
                Isbn.Create("9780262033848"),
                "Introduction to Algorithms",
                technology.Id,
                publishedOn: new DateOnly(2009, 7, 31),
                language: "en",
                pageCount: 1292,
                description: "Comprehensive reference on algorithm design and analysis."),
                [],
                [programming.Id, reference.Id],
                2),

            (Book.Create(
                Isbn.Create("9780553380163"),
                "A Brief History of Time",
                nonFiction.Id,
                publisherId: penguin.Id,
                publishedOn: new DateOnly(1998, 9, 1),
                language: "en",
                pageCount: 212,
                description: "Cosmology for the general reader."),
                [],
                [popularScience.Id],
                3),
        };

        foreach ((Book book, _, _, _) in seedBooks)
        {
            _context.Books.Add(book);
        }

        // Books must be saved before the joins and copies, because those carry
        // the book's key - which the database assigns on insert.
        await _context.SaveChangesAsync(cancellationToken);

        int barcodeSeed = 1000;

        foreach ((Book book, int[] authorIds, int[] genreIds, int copyCount) in seedBooks)
        {
            if (authorIds.Length > 0)
            {
                book.SetAuthors(authorIds);
            }

            book.SetGenres(genreIds);

            for (int i = 0; i < copyCount; i++)
            {
                // Deterministic, readable barcodes: LIB-001000, LIB-001001, ...
                book.AddCopy(
                    barcode: $"LIB-{barcodeSeed++:D6}",
                    condition: i == 0 ? CopyCondition.New : CopyCondition.Good,
                    shelfLocation: $"{book.Title[0]}-{book.Id:D2}-{i + 1}",
                    acquiredOn: new DateOnly(2024, 1, 1).AddDays(barcodeSeed % 365));
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        int totalCopies = seedBooks.Sum(b => b.CopyCount);
        LogSeedCompleted(seedBooks.Count, totalCopies);

        await SeedMembersAsync(cancellationToken);
        await SeedLoansAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds membership types and a spread of members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guarded separately from the catalogue so it still runs on a database
    /// seeded before members existed — the outer check returns early once any
    /// book is present, which would otherwise leave an upgraded database with a
    /// catalogue and no members.
    /// </para>
    /// <para>
    /// The members deliberately cover every <c>MemberStatus</c>, so Phase 4's
    /// "only an active member may borrow" rule has something to fail against
    /// without a tester having to construct it first.
    /// </para>
    /// </remarks>
    private async Task SeedMembersAsync(CancellationToken cancellationToken)
    {
        if (await _context.Members.AnyAsync(cancellationToken))
        {
            return;
        }

        // Loan limits and periods that Phase 4 reads directly: MaxConcurrentLoans
        // gates issuing, LoanPeriodDays computes DueAt.
        MembershipType standard = MembershipType.Create(
            "Standard", maxConcurrentLoans: 5, loanPeriodDays: 14,
            "General adult membership.");

        MembershipType student = MembershipType.Create(
            "Student", maxConcurrentLoans: 8, loanPeriodDays: 28,
            "Longer loans for study; requires proof of enrolment.");

        MembershipType staff = MembershipType.Create(
            "Staff", maxConcurrentLoans: 15, loanPeriodDays: 42,
            "Library and academic staff.");

        MembershipType junior = MembershipType.Create(
            "Junior", maxConcurrentLoans: 3, loanPeriodDays: 21,
            "Under 16. Restricted to the children's collection.");

        _context.MembershipTypes.AddRange(standard, student, staff, junior);
        await _context.SaveChangesAsync(cancellationToken);

        var seedMembers = new List<(Member Member, Action<Member>? AfterSave)>
        {
            (NewMember("Priya Sharma", "priya.sharma@example.com", standard.Id,
                new DateOnly(2024, 3, 12), "+91 98765 43210"), null),

            (NewMember("Arjun Mehta", "arjun.mehta@example.com", student.Id,
                new DateOnly(2025, 9, 1), "+91 91234 56789"), null),

            (NewMember("Fatima Khan", "fatima.khan@example.com", staff.Id,
                new DateOnly(2022, 1, 17), "+91 99887 76655"), null),

            (NewMember("Rohan Desai", "rohan.desai@example.com", standard.Id,
                new DateOnly(2025, 6, 30), null), null),

            (NewMember("Ananya Iyer", "ananya.iyer@example.com", junior.Id,
                new DateOnly(2026, 2, 14), "+91 90000 11111"), null),

            (NewMember("Vikram Nair", "vikram.nair@example.com", student.Id,
                new DateOnly(2024, 11, 5), null), null),

            // Suspended: gives Phase 4 a member who must be refused a loan.
            (NewMember("Kabir Singh", "kabir.singh@example.com", standard.Id,
                new DateOnly(2023, 8, 22), "+91 98111 22333"),
                m => m.Suspend("Unpaid fines exceeding 500 rupees.")),

            // Expired: lapsed through time rather than conduct.
            (NewMember("Meera Joshi", "meera.joshi@example.com", standard.Id,
                new DateOnly(2021, 4, 3), null),
                m => m.Expire()),

            // Cancelled: terminal, and retained so loan history survives.
            (NewMember("Sanjay Gupta", "sanjay.gupta@example.com", standard.Id,
                new DateOnly(2020, 7, 19), null),
                m => m.Cancel("Relocated.")),
        };

        foreach ((Member member, _) in seedMembers)
        {
            _context.Members.Add(member);
        }

        // Saved before the membership numbers are assigned, because each number
        // is derived from the id the database issues on insert.
        await _context.SaveChangesAsync(cancellationToken);

        foreach ((Member member, Action<Member>? afterSave) in seedMembers)
        {
            member.AssignMembershipNumber();
            afterSave?.Invoke(member);
        }

        await _context.SaveChangesAsync(cancellationToken);

        LogMembersSeeded(seedMembers.Count, 4);
    }

    /// <summary>
    /// Seeds a realistic lending history: loans out, loans overdue, loans closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an overdue loan is seeded deliberately.</b> Overdue behaviour is the
    /// one part of this system that cannot be reached by calling the API and
    /// waiting — a fresh loan is not late for a fortnight. Without a back-dated
    /// row, the overdue report and the fine assessment are unreachable in a demo
    /// and unverifiable by hand.
    /// </para>
    /// <para>
    /// Dates are relative to <see cref="IClock"/> rather than hard-coded, so the
    /// overdue loan is still overdue whenever the database is rebuilt. A fixed
    /// date would be correct on the day it was written and wrong every day after.
    /// </para>
    /// <para>
    /// Every loan is created through <c>Loan.Issue</c>, so the seed data obeys the
    /// same invariants as real input — including marking each copy <c>OnLoan</c>,
    /// which is what keeps <c>BookCopy.Status</c> honest about what is on the
    /// shelf.
    /// </para>
    /// </remarks>
    private async Task SeedLoansAsync(CancellationToken cancellationToken)
    {
        if (await _context.Loans.AnyAsync(cancellationToken))
        {
            return;
        }

        List<Member> members = await _context.Members
            .Include(m => m.MembershipType)
            .Where(m => m.Status == MemberStatus.Active)
            .OrderBy(m => m.Id)
            .Take(4)
            .ToListAsync(cancellationToken);

        List<BookCopy> copies = await _context.BookCopies
            .Where(c => c.Status == CopyStatus.Available)
            .OrderBy(c => c.Id)
            .Take(6)
            .ToListAsync(cancellationToken);

        if (members.Count < 3 || copies.Count < 5)
        {
            return;
        }

        DateTimeOffset now = _clock.UtcNow;

        // Out and not yet due - the ordinary case.
        Loan active = Loan.Issue(copies[0], members[0], now.AddDays(-3),
            members[0].MembershipType.LoanPeriodDays);

        // Out and overdue. Issued 30 days ago on a 14-day period, so it is 16 days
        // late and returning it assesses a fine of 16 x the configured rate.
        Loan overdue = Loan.Issue(copies[1], members[1], now.AddDays(-30), 14);

        // Badly overdue, to give the chase list something with a range in it.
        Loan veryOverdue = Loan.Issue(copies[2], members[2], now.AddDays(-75), 14);

        // Closed on time - history, and proof that the partial index permits a
        // copy to be lent again once it is back.
        Loan returnedOnTime = Loan.Issue(copies[3], members[0], now.AddDays(-60), 14);
        returnedOnTime.Return(now.AddDays(-50));

        // Closed late. No Fine row is created here: fines are assessed by the
        // post-commit handler, and inventing one directly would model an outcome
        // the running system never produces that way.
        Loan returnedLate = Loan.Issue(copies[4], members[1], now.AddDays(-90), 14);
        returnedLate.Return(now.AddDays(-70));

        _context.Loans.AddRange(active, overdue, veryOverdue, returnedOnTime, returnedLate);

        // Clears the events the two returns collected. Nothing should dispatch
        // from seeding: these are historical facts being recorded, not things
        // happening now, and a seeded return must not notify anyone or assess a
        // fine six weeks after the fact.
        returnedOnTime.ClearDomainEvents();
        returnedLate.ClearDomainEvents();

        await _context.SaveChangesAsync(cancellationToken);

        LogLoansSeeded(5);
    }

    private static Member NewMember(
        string fullName, string email, int membershipTypeId, DateOnly joinedOn, string? phone)
    {
        // Through the value objects and the factory, so seed data passes exactly
        // the validation real input does. A malformed address here fails loudly
        // at startup rather than quietly populating a broken row.
        return Member.Create(
            fullName,
            Email.Create(email),
            membershipTypeId,
            joinedOn,
            phone is null ? null : PhoneNumber.Create(phone));
    }

    // Source-generated logging - see GlobalExceptionHandler for why analyzer
    // CA1848 insists on this over _logger.LogInformation("...", args).

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Seeded {MemberCount} members across {TypeCount} membership types.")]
    private partial void LogMembersSeeded(int memberCount, int typeCount);

    [LoggerMessage(
        EventId = 7005,
        Level = LogLevel.Information,
        Message = "Seeded {LoanCount} loans")]
    private partial void LogLoansSeeded(int loanCount);

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Catalogue already seeded; skipping.")]
    private partial void LogAlreadySeeded();

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Seeding catalogue...")]
    private partial void LogSeedingStarted();

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Seeded {BookCount} books with {CopyCount} copies.")]
    private partial void LogSeedCompleted(int bookCount, int copyCount);
}
