using Library.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Library.IntegrationTests;

/// <summary>
/// Boots the real API in-process, backed by a private SQLite database.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys over a unit test.</b> Unit tests verify logic in isolation
/// and are blind to everything between the HTTP request and that logic: routing,
/// model binding, middleware order, JSON casing, EF Core mappings, and the SQL
/// actually generated. Those are exactly where integration bugs live. This
/// factory runs the genuine <c>Program.cs</c> - the same DI container, the same
/// pipeline - over an in-memory transport, so there is no port to bind and no
/// server to start.
/// </para>
/// <para>
/// <b>Why SQLite and not EF Core's InMemory provider.</b> The InMemory provider
/// is not a relational database: it ignores unique indexes, foreign keys, and
/// check constraints. A test suite built on it would happily let two active
/// loans exist for the same copy - which is precisely the rule this system is
/// built to enforce. SQLite honours those constraints, so the tests actually
/// test them.
/// </para>
/// <para>
/// <b>Isolation.</b> Each factory instance gets its own uniquely-named database
/// file, created on construction and deleted on disposal. Test classes therefore
/// cannot see one another's rows, and xUnit is free to run them in parallel.
/// </para>
/// </remarks>
public class LibraryApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"library-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // UseSetting writes into configuration BEFORE the host reads it, which
        // makes it the cleanest override point: no need to remove and re-add the
        // DbContext registration afterwards.
        builder.UseSetting("Database:Provider", DatabaseProviders.Sqlite);
        builder.UseSetting("Database:ConnectionString", $"Data Source={_databasePath}");

        // Migrations are applied explicitly in InitializeAsync so a failure
        // surfaces as a test error rather than as a startup crash.
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");

        builder.ConfigureServices(services =>
        {
            // Later phases replace real collaborators here - for example binding
            // IClock to a FakeClock so a test can fast-forward past a due date.
            _ = services;
        });
    }

    /// <summary>Creates the schema before the first test in the class runs.</summary>
    public async ValueTask InitializeAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        // EnsureCreated builds the schema straight from the model, skipping the
        // migration history. It is the right choice while the model has no
        // migrations yet (Phase 1). From Phase 2 this becomes MigrateAsync(), so
        // the tests exercise the same migrations that ship to production.
        await context.Database.EnsureCreatedAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);

        // Best-effort cleanup. A leftover temp file is untidy but harmless, and
        // must never be the reason a test run fails.
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // The connection pool may still hold the file handle briefly.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
