using System.Reflection;
using Library.Application.Common.Abstractions;
using Library.Domain.Common;
using Library.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Discovers every IEntityTypeConfiguration<T> in Library.Infrastructure.
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
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
