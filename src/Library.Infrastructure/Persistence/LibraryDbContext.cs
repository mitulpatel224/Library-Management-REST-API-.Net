using System.Reflection;
using Library.Application.Common.Abstractions;
using Library.Domain.Common;
using Library.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Library.Infrastructure.Persistence;

/// <summary>
/// The EF Core unit of work for the whole system.
/// </summary>
/// <remarks>
/// <para>
/// Entity sets are added in Phase 2 onwards. What is set up here is the
/// machinery every entity will rely on:
/// </para>
/// <list type="number">
///   <item>
///     <b>Configuration by convention-free assembly scan.</b>
///     <see cref="OnModelCreating"/> picks up every
///     <c>IEntityTypeConfiguration&lt;T&gt;</c> in this assembly. Mapping lives in
///     one class per entity, beside the other mappings - not as attributes
///     sprinkled over the domain types. This is what keeps
///     <c>Library.Domain</c> free of any EF Core reference: the domain describes
///     the business, the configuration describes the table, and neither leaks
///     into the other.
///   </item>
///   <item>
///     <b>Central audit stamping.</b> <see cref="SaveChangesAsync"/> fills in
///     <c>CreatedAt</c> / <c>UpdatedAt</c> for every
///     <see cref="AuditableEntity"/> being saved. Doing it here rather than in
///     each service means it cannot be forgotten in the one place it matters.
///   </item>
/// </list>
/// <para>
/// <b>Why no repository wrapper over <c>DbSet</c> by default?</b> <c>DbContext</c>
/// already is a unit of work and <c>DbSet&lt;T&gt;</c> already is a repository.
/// This project still defines repository interfaces in the Application layer -
/// but for a specific reason, not as ritual: it is what stops <c>IQueryable</c>
/// and EF Core types from reaching the controllers, and it is what makes the
/// Application layer unit-testable without a database.
/// </para>
/// </remarks>
public class LibraryDbContext : DbContext
{
    private readonly IClock _clock;

    public LibraryDbContext(DbContextOptions<LibraryDbContext> options, IClock clock)
        : base(options)
        => _clock = clock;

    // -------------------------------------------------------------------
    // Entity sets.
    //
    // Exposed for the catalogue aggregate and its lookups. Note that there is
    // no DbSet for BookAuthor or BookGenre: those are reached through their
    // parents, and EF Core still maps them because their configuration classes
    // are discovered by the assembly scan below.
    // -------------------------------------------------------------------
    public DbSet<Book> Books => Set<Book>();

    public DbSet<BookCopy> BookCopies => Set<BookCopy>();

    public DbSet<Author> Authors => Set<Author>();

    public DbSet<Genre> Genres => Set<Genre>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Publisher> Publishers => Set<Publisher>();

    public DbSet<Member> Members => Set<Member>();

    public DbSet<MembershipType> MembershipTypes => Set<MembershipType>();

    public DbSet<Loan> Loans => Set<Loan>();

    public DbSet<Fine> Fines => Set<Fine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Discovers every IEntityTypeConfiguration<T> in Library.Infrastructure.
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        ApplySqliteDateTimeOffsetConversion(modelBuilder);
        ApplyActiveLoanIndexFilter(modelBuilder);
    }

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as a UTC <see cref="DateTime"/>
    /// when running on SQLite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, <c>ORDER BY</c> on any timestamp throws.</b> SQLite has no
    /// date type: the provider stores a <c>DateTimeOffset</c> as TEXT with its
    /// offset appended — <c>2026-09-13 16:54:21.647+00:00</c> — and text sorts
    /// lexicographically. Two instants an hour apart in different offsets would
    /// sort in the wrong order, so EF Core refuses to translate the ordering at
    /// all rather than return a wrong answer quietly. <c>GET /api/loans</c> sorts
    /// by due date, so it met that refusal as a 500.
    /// </para>
    /// <para>
    /// Converting to UTC loses nothing here: every timestamp in this system comes
    /// from <c>IClock.UtcNow</c>, so the offset is always zero and carries no
    /// information. What it gains is a stored form —
    /// <c>2026-09-13 16:54:21.647</c> — whose lexicographic order <i>is</i>
    /// chronological order.
    /// </para>
    /// <para>
    /// SQL Server is left alone: <c>datetimeoffset</c> is a real type there, sorts
    /// correctly, and keeps the offset. This is the shape of provider difference
    /// the architecture rule is about — it lives in Infrastructure, and nothing
    /// above this layer can tell the two apart.
    /// </para>
    /// </remarks>
    private void ApplySqliteDateTimeOffsetConversion(ModelBuilder modelBuilder)
    {
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) != true)
        {
            return;
        }

        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

        var nullableConverter = new ValueConverter<DateTimeOffset?, DateTime?>(
            value => value.HasValue ? value.Value.UtcDateTime : null,
            value => value.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
                : null);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(converter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableConverter);
                }
            }
        }
    }

    /// <summary>
    /// Attaches the partial-index filter that enforces "one active loan per copy".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from <see cref="Configurations.LoanConfiguration"/> because
    /// <c>HasFilter</c> takes raw SQL that EF Core emits verbatim — identifier
    /// quoting included — and the two providers quote differently. The feature
    /// exists in both (SQL Server "filtered index", SQLite "partial index"); only
    /// the text differs.
    /// </para>
    /// <para>
    /// An unknown provider throws rather than falling back to no filter. Without
    /// the filter the index would be unique across <i>all</i> loans for a copy,
    /// which would reject the second time any book was ever lent — a failure worth
    /// hitting at startup rather than in front of a borrower.
    /// </para>
    /// </remarks>
    private void ApplyActiveLoanIndexFilter(ModelBuilder modelBuilder)
    {
        string? provider = Database.ProviderName;

        string filter = provider switch
        {
            not null when provider.Contains("Sqlite", StringComparison.Ordinal) =>
                "\"ReturnedAt\" IS NULL",

            not null when provider.Contains("SqlServer", StringComparison.Ordinal) =>
                "[ReturnedAt] IS NULL",

            _ => throw new InvalidOperationException(
                $"No active-loan index filter is defined for provider '{provider}'. " +
                "Add one before using this provider: without the filter the index " +
                "would reject the second loan of every copy."),
        };

        modelBuilder.Entity<Loan>()
            .HasIndex(l => l.BookCopyId)
            .HasDatabaseName(Configurations.LoanConfiguration.ActiveLoanIndexName)
            .HasFilter(filter);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAuditFields();
        return base.SaveChanges();
    }

    /// <summary>
    /// Writes creation and modification timestamps from the injected clock.
    /// </summary>
    private void StampAuditFields()
    {
        DateTimeOffset now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    // Guard against a caller overwriting the original creation
                    // time by posting it back in an update payload.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
            }
        }
    }
}
