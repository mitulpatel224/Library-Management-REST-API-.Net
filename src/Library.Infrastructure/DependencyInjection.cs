using Library.Application.Books;
using Library.Application.Books.Import;
using Library.Application.Common.Abstractions;
using Library.Application.Loans;
using Library.Application.Members;
using Library.Application.Reports;
using Library.Application.Reports.Export;
using Library.Application.Notifications;
using Library.Infrastructure.Notifications;
using Library.Infrastructure.Persistence;
using Library.Infrastructure.Repositories;
using Library.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Library.Infrastructure;

/// <summary>
/// Binds every Application-layer abstraction to its concrete implementation.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();   // fail at boot, not on first request

        // Singleton: stateless, thread-safe, and needed by the DbContext factory
        // below - a scoped clock could not be resolved from the root provider.
        services.AddSingleton<IClock, SystemClock>();

        // FineOptions lives in Library.Application - the fine handler depends on
        // it - but binding it is an infrastructure concern, and Application has no
        // configuration reference to do it with. Declared there, wired here.
        services.AddOptions<FineOptions>()
            .Bind(configuration.GetSection(FineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Exposed as a delegate rather than IOptions<FineOptions> so a consumer
        // cannot reach fields it has no business reading, and can be tested with
        // () => 50m instead of options plumbing.
        //
        // IOptionsMonitor, not IOptions: the rate is read at the moment a fine is
        // assessed, so an appsettings edit takes effect without a restart.
        services.AddSingleton<FineRateResolver>(provider =>
        {
            IOptionsMonitor<FineOptions> options =
                provider.GetRequiredService<IOptionsMonitor<FineOptions>>();

            return () => options.CurrentValue.RatePerDay;
        });

        DatabaseOptions dbOptions = configuration
            .GetSection(DatabaseOptions.SectionName)
            .Get<DatabaseOptions>() ?? new DatabaseOptions();

        services.AddDbContext<LibraryDbContext>((serviceProvider, builder) =>
        {
            ConfigureProvider(builder, dbOptions);

            if (dbOptions.EnableSensitiveDataLogging)
            {
                // Logs parameter VALUES. Development only - see DatabaseOptions.
                builder.EnableSensitiveDataLogging();
                builder.EnableDetailedErrors();
            }
        });

        // Repository implementations, bound to the interfaces declared in
        // Library.Application. This line is the runtime half of Dependency
        // Inversion: at compile time Application knows nothing of this project,
        // and at startup this project supplies what Application asked for.
        services.AddScoped<IBookRepository, BookRepository>();
        services.AddScoped<IMemberRepository, MemberRepository>();
        services.AddScoped<ILoanRepository, LoanRepository>();
        services.AddScoped<IFineRepository, FineRepository>();

        // Scoped: it reads the ChangeTracker of the request's DbContext.
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Singleton so subscribers attached at startup stay attached. A scoped
        // notifier would drop every subscription at the end of each request.
        services.AddSingleton<INotificationService, NotificationService>();

        // Import readers. Registered as a SET so BookImportReaderFactory can
        // index them by format - adding XML later is one more AddScoped here and
        // no change to the factory or the service.
        services.AddScoped<IBookImportReader, Import.CsvBookImportReader>();
        services.AddScoped<IBookImportReader, Import.JsonBookImportReader>();
        services.AddScoped<IBookImportReaderFactory, Import.BookImportReaderFactory>();

        // Scoped, and deliberately stateful for the life of one import: it caches
        // every lookup it resolves, so a 10,000-row feed does not issue 10,000
        // near-identical queries for the same publisher.
        services.AddScoped<ILookupResolver, Import.LookupResolver>();

        services.AddScoped<IReportRepository, ReportRepository>();

        // Exporters, registered as a set so ReportExporterFactory can index them
        // by format - the same Open/Closed shape as the import readers.
        services.AddScoped<IReportExporter, Export.CsvReportExporter>();
        services.AddScoped<IReportExporter, Export.JsonReportExporter>();
        services.AddScoped<IReportExporterFactory, Export.ReportExporterFactory>();

        // Currency alongside the rate, both bound from FineOptions. See
        // FineCurrencyResolver for why they are two delegates and not one.
        services.AddSingleton<FineCurrencyResolver>(provider =>
        {
            IOptionsMonitor<FineOptions> options =
                provider.GetRequiredService<IOptionsMonitor<FineOptions>>();

            return () => options.CurrentValue.Currency;
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<DatabaseSeeder>();

        return services;
    }

    /// <summary>
    /// Selects the EF Core provider from configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This single method is what makes "SQLite now, SQL Server later" a config
    /// change rather than a rewrite. It works only as long as one rule holds
    /// everywhere else in the solution: <b>no provider-specific SQL, types, or
    /// functions escape this project.</b> The moment a raw <c>NVARCHAR(MAX)</c>
    /// or a <c>DATEADD</c> call appears in a query, the swap stops being free.
    /// </para>
    /// <para>
    /// Migrations are kept in separate assemblies per provider because the
    /// generated DDL genuinely differs - SQLite cannot <c>ALTER COLUMN</c>, so
    /// EF Core rewrites tables for changes SQL Server does in place. One shared
    /// migration set would produce SQL that is wrong for one of the two.
    /// </para>
    /// </remarks>
    private static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        switch (options.Provider)
        {
            case DatabaseProviders.SqlServer:
                builder.UseSqlServer(options.ConnectionString, sql =>
                {
                    sql.MigrationsAssembly("Library.Migrations.SqlServer");
                    sql.CommandTimeout(options.CommandTimeoutSeconds);
                    // Retry on transient faults (deadlock, timeout, failover).
                    sql.EnableRetryOnFailure(maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                });
                break;

            case DatabaseProviders.Sqlite:
                builder.UseSqlite(options.ConnectionString, sqlite =>
                {
                    sqlite.MigrationsAssembly("Library.Infrastructure");
                    sqlite.CommandTimeout(options.CommandTimeoutSeconds);
                });
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider '{options.Provider}'. " +
                    $"Expected '{DatabaseProviders.Sqlite}' or '{DatabaseProviders.SqlServer}'.");
        }
    }
}
