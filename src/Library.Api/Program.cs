using Library.Api.Middleware;
using Library.Api.OpenApi;
using Library.Application;
using Library.Infrastructure;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

// ---------------------------------------------------------------------------
// Bootstrap logger.
//
// Serilog is configured twice on purpose. This first, minimal configuration is
// active only while the host is being built, so that a failure during startup -
// a bad connection string, a missing option - is still logged somewhere instead
// of vanishing. It is replaced below by the configuration-driven logger.
// ---------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Library Management API");

    var builder = WebApplication.CreateBuilder(args);

    // Reads the Serilog section of appsettings.json, so log levels and sinks are
    // deployment configuration rather than compiled-in code.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // -----------------------------------------------------------------------
    // Service registration (the composition root).
    //
    // Each layer contributes one extension method and nothing else. Program.cs
    // therefore names four method calls rather than fifty types, and adding a
    // service to a layer never touches this file.
    // -----------------------------------------------------------------------
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddControllers();

    // ProblemDetails (RFC 9457) as the uniform error shape for the whole API.
    // Registering it also converts framework-generated failures - 404 on an
    // unmatched route, 405 on a wrong verb - into the same JSON shape, so a
    // client never has to parse two different error formats.
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // OpenAPI document generation is built into ASP.NET Core 9+; Swashbuckle is
    // used further down for the UI only. See OpenApiConfiguration for why.
    builder.Services.AddOpenApiDocument();

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<LibraryDbContext>(
            name: "database",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
            tags: ["ready"]);

    var app = builder.Build();

    // -----------------------------------------------------------------------
    // HTTP pipeline.
    //
    // ORDER IS BEHAVIOUR HERE, NOT STYLE. Each component wraps the ones after
    // it, so a component only sees what the ones before it have already done.
    // The exception handler is first because it must wrap everything; auth
    // comes before endpoints because endpoints need an authenticated principal.
    // -----------------------------------------------------------------------

    // FIRST: catches exceptions thrown by everything downstream.
    app.UseExceptionHandler();

    // Re-shapes bare status codes (404 from an unmatched route) into ProblemDetails.
    app.UseStatusCodePages();

    // One structured log line per request, with method, path, status and duration -
    // instead of the two noisy lines ASP.NET Core logs by default.
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();               // serves /openapi/v1.json
        app.UseSwaggerUiForOpenApi();   // serves /swagger, reading that document
    }

    app.UseHttpsRedirection();

    // Authentication and authorization are added in Phase 5. The order they go
    // in - UseAuthentication BEFORE UseAuthorization - is the single most common
    // pipeline mistake: reversed, authorization runs against an anonymous
    // principal and every [Authorize] endpoint returns 401 no matter the token.

    app.MapControllers();

    // Liveness: is the process up? Deliberately does NOT touch the database -
    // an orchestrator restarting the app because the database blipped turns a
    // recoverable outage into an outage plus a restart loop.
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false,
    });

    // Readiness: can it serve traffic? This one does check the database.
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
    });

    await ApplyMigrationsIfConfiguredAsync(app);

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Library Management API terminated unexpectedly");
    throw;
}
finally
{
    // Flushes any buffered log entries before the process exits. Without this,
    // the log line explaining a crash is the one most likely to be lost.
    await Log.CloseAndFlushAsync();
}

// Applies pending EF Core migrations when Database:ApplyMigrationsOnStartup is set.
//
// Convenient for development and for integration tests. It is off by default and
// should stay off in production: it gives the application schema-change rights it
// otherwise does not need, and two instances starting simultaneously race each
// other. Production runs migrations as a separate, reviewable deployment step.
//
// NOTE: plain // comments, not /// XML docs - a local function is not a valid
// target for an XML documentation comment (CS1587).
static async Task ApplyMigrationsIfConfiguredAsync(WebApplication app)
{
    var options = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;

    if (!options.ApplyMigrationsOnStartup)
    {
        return;
    }

    // A scope is required: LibraryDbContext is scoped, and resolving a scoped
    // service from the root provider is a captive-dependency bug.
    using IServiceScope scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

    Log.Information("Applying pending migrations ({Provider})", options.Provider);
    await context.Database.MigrateAsync();
    Log.Information("Migrations applied");

    // Seeding rides along with the same development-only switch. The seeder is
    // itself a no-op once any book exists, so restarting the app repeatedly does
    // not duplicate the catalogue.
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> in the integration test
/// project can boot this exact application.
/// </summary>
/// <remarks>
/// A top-level-statements Program class is <c>internal</c> by default, which the
/// test project cannot reach. Declaring the partial class here - rather than
/// making the assembly's internals visible - keeps the seam explicit.
/// </remarks>
public partial class Program;
