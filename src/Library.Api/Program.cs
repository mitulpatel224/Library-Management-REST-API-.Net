using Library.Api.Filters;
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

    // The validation filter runs every FluentValidation validator for every
    // action argument that has one, so an unvalidated request is impossible
    // rather than merely unlikely.
    builder.Services
        .AddControllers(options => options.Filters.Add<ValidationFilter>())
        .AddJsonOptions(options =>
        {
            // -------------------------------------------------------------
            // Reject JSON properties the request DTO does not declare.
            //
            // By default System.Text.Json SILENTLY DISCARDS unknown members.
            // Combined with narrow request DTOs - which exist to prevent mass
            // assignment - that produces a genuinely misleading API: a caller
            // PUTs a full object back with a changed `barcode`, receives 200,
            // and reasonably concludes the barcode changed. It did not.
            //
            // The DTO was right to ignore it; the 200 was wrong. Disallow turns
            // that into a 400 naming the offending property, so the contract is
            // enforced out loud instead of silently.
            //
            // This is strict, and deliberately so: quietly accepting input you
            // do not honour is how clients end up depending on behaviour that
            // was never real.
            // -------------------------------------------------------------
            options.JsonSerializerOptions.UnmappedMemberHandling =
                System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;

            // -------------------------------------------------------------
            // Enums on the wire as names, not ordinals.
            //
            // MemberStatus.Active serialised as 0, which pushes two problems
            // onto every client. The response is unreadable without the enum
            // definition beside it - "status": 0 says nothing - and branching
            // on the number silently changes meaning the day a value is
            // inserted in the middle. MemberStatus already numbers its members
            // explicitly to survive that, which only protects the DATABASE;
            // this protects the API.
            //
            // It also matches the stated error contract, where clients branch
            // on stable strings (ErrorCode) rather than magic numbers.
            //
            // Reads stay permissive: the converter accepts the name and the
            // number, so a client sending 0 today keeps working.
            // -------------------------------------------------------------
            options.JsonSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        })
        .ConfigureApiBehaviorOptions(options =>
        {
            // Model binding fails BEFORE any action runs, so GlobalExceptionHandler
            // never sees it and MVC writes its own 400 - a different shape, with no
            // errorCode, naming our internal types. See RequestProblemDetails.
            options.InvalidModelStateResponseFactory =
                RequestProblemDetails.ForInvalidModelState;
        });

    // ProblemDetails (RFC 9457) as the uniform error shape for the whole API.
    // Registering it also converts framework-generated failures - 404 on an
    // unmatched route, 405 on a wrong verb - into the same JSON shape, so a
    // client never has to parse two different error formats.
    builder.Services.AddProblemDetails(options =>
        options.CustomizeProblemDetails = RequestProblemDetails.Customize);
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
