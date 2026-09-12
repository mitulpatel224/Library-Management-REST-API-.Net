using Library.Application.Common.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Library.Infrastructure.Persistence;

/// <summary>
/// Builds a <see cref="LibraryDbContext"/> for the <c>dotnet ef</c> tools.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>dotnet ef</c> must construct a DbContext to read
/// the model, but <see cref="LibraryDbContext"/> has no parameterless
/// constructor - it takes options and an <see cref="IClock"/>. Without a factory
/// the tools fall back to booting the API project's host, which forces the API
/// project to reference <c>Microsoft.EntityFrameworkCore.Design</c>.
/// </para>
/// <para>
/// That reference would be wrong: Design is build-time tooling, and the API
/// layer's job is to serve HTTP, not to know how migrations are scaffolded.
/// Supplying this factory keeps EF tooling inside the one project that already
/// owns persistence, so migration commands run against
/// <c>--project src/Library.Infrastructure</c> with no startup project at all.
/// </para>
/// <para>
/// <b>Why no appsettings.json is read here.</b> Scaffolding a migration needs
/// the provider - so EF knows which SQL dialect to emit - but never opens a
/// connection. A placeholder connection string is therefore sufficient, and
/// avoids dragging the JSON configuration packages into this project purely for
/// a design-time concern. Override the provider when scaffolding for SQL Server:
/// </para>
/// <code>
/// LIBRARY_DB_PROVIDER=SqlServer dotnet ef migrations add Name --project src/Library.Infrastructure
/// </code>
/// <para>
/// This type is never constructed at runtime; only the CLI tools use it.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    /// <summary>Environment variable selecting the provider to scaffold against.</summary>
    public const string ProviderVariable = "LIBRARY_DB_PROVIDER";

    public LibraryDbContext CreateDbContext(string[] args)
    {
        string provider =
            Environment.GetEnvironmentVariable(ProviderVariable) ?? DatabaseProviders.Sqlite;

        DbContextOptionsBuilder<LibraryDbContext> builder = new();

        if (string.Equals(provider, DatabaseProviders.SqlServer, StringComparison.OrdinalIgnoreCase))
        {
            builder.UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=LibraryDb;Trusted_Connection=True",
                sql => sql.MigrationsAssembly("Library.Migrations.SqlServer"));
        }
        else
        {
            builder.UseSqlite(
                "Data Source=library-design-time.db",
                sqlite => sqlite.MigrationsAssembly("Library.Infrastructure"));
        }

        // No SaveChanges happens during scaffolding, so the clock is never read.
        return new LibraryDbContext(builder.Options, new DesignTimeClock());
    }

    private sealed class DesignTimeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UnixEpoch.UtcDateTime);
    }
}
