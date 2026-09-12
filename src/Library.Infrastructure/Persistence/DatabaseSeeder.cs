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
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(LibraryDbContext context, ILogger<DatabaseSeeder> logger)
    {
        _context = context;
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
    }

    // Source-generated logging - see GlobalExceptionHandler for why analyzer
    // CA1848 insists on this over _logger.LogInformation("...", args).

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
