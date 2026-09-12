# Setup and Run

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0.100+** | Verify with `dotnet --version` |
| Git | any recent | |
| IDE | VS 2022 **17.14+**, VS Code + C# Dev Kit, or Rider 2025.3+ | 17.14 is the minimum that opens `.slnx` |

**No database server is required.** Development runs on SQLite, and the database
file is created on first run.

---

## Clone and run

```bash
git clone https://github.com/mitulpatel224/Library-Management-REST-API-.Net.git
cd "Library Management - Assessment 1"

dotnet restore LibraryManagement.slnx
dotnet build   LibraryManagement.slnx -c Release
dotnet run --project src/Library.Api
```

Expected output:

```
[INF] Starting Library Management API
[INF] Applying pending migrations (Sqlite)
[INF] Migrations applied
[INF] Seeding catalogue...
[INF] Seeded 15 books with 41 copies.
[INF] Now listening on: http://localhost:5112
```

Open **<http://localhost:5112/swagger>**.

> **Port note:** the port comes from
> `src/Library.Api/Properties/launchSettings.json`, which `dotnet run` reads and
> which **overrides** the `ASPNETCORE_URLS` environment variable. To use a
> different port, edit that file or run the built binary directly:
> ```bash
> ASPNETCORE_URLS=http://localhost:5199 \
>   dotnet src/Library.Api/bin/Release/net10.0/Library.Api.dll
> ```

### First-run behaviour

In the Development environment only, startup applies migrations **and** seeds the
catalogue. Both are controlled by `appsettings.Development.json`:

```json
"Database": {
  "ApplyMigrationsOnStartup": true,
  "EnableSensitiveDataLogging": true
}
```

Both default to `false` elsewhere, deliberately:

- `ApplyMigrationsOnStartup` grants the running application schema-modification
  rights it should not have in production, and two instances starting at once
  will race. Real deployments run `dotnet ef database update` as a separate,
  reviewable step.
- `EnableSensitiveDataLogging` writes SQL **parameter values** to the log. From
  Phase 5 those include password hashes and personal data.

The seeder is a no-op once any book exists, so restarting does not duplicate
anything.

---

## Verifying it works

```bash
curl http://localhost:5112/health/live          # Healthy - process only
curl http://localhost:5112/health/ready         # Healthy - includes the database

curl "http://localhost:5112/api/books?pageSize=3"
curl "http://localhost:5112/api/books?search=design"
curl "http://localhost:5112/api/books?author=Fowler"
curl "http://localhost:5112/api/books/isbn/978-0-13-235088-4"
curl "http://localhost:5112/api/books/1"
curl "http://localhost:5112/api/books/1/copies"
```

Worth trying, because they demonstrate the safety controls:

```bash
# Sort whitelist: returns 200, sorted by title. The input is looked up,
# never interpolated into SQL.
curl "http://localhost:5112/api/books?sortBy=Title;DROP%20TABLE%20Books--"

# Page size clamped server-side to 100.
curl "http://localhost:5112/api/books?pageSize=99999"

# RFC 9457 ProblemDetails with a stable errorCode and a traceId.
curl "http://localhost:5112/api/books/9999"
```

---

## Running the tests

```bash
dotnet build LibraryManagement.slnx -c Release

./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe
./tests/Library.IntegrationTests/bin/Release/net10.0/Library.IntegrationTests.exe
```

Expected: 26 unit tests and 5 integration tests, all passing.

> **Known issue.** `dotnet test` currently fails with *"Testing with VSTest target
> is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later"*.
> The .NET 10 SDK retired the VSTest bridge; xUnit v3 targets Microsoft.Testing
> Platform instead, and the runner configuration is not yet resolved. Because
> xUnit v3 builds each test project as an executable, running them directly works
> and is the documented workaround. Tracked as issue 1 in
> [`../TASKS.md`](../TASKS.md), and it blocks the CI workflow.

---

## Working with migrations

Migrations run against `Library.Infrastructure` **alone** — no startup project
needed, because `DesignTimeDbContextFactory` supplies the context.

```bash
# Add
dotnet ef migrations add <Name> \
  --project src/Library.Infrastructure \
  --context LibraryDbContext \
  --output-dir Persistence/Migrations

# Review the SQL BEFORE applying it
dotnet ef migrations script --project src/Library.Infrastructure --context LibraryDbContext

# Apply
dotnet ef database update --project src/Library.Infrastructure --context LibraryDbContext

# Undo the last (unapplied) migration
dotnet ef migrations remove --project src/Library.Infrastructure --context LibraryDbContext
```

If `dotnet ef` is missing or out of date:

```bash
dotnet tool update --global dotnet-ef
```

**Always read the generated migration before applying it.** EF Core occasionally
infers a destructive operation — most often a drop-and-recreate where you
expected an alter — and the migration file is the last point at which that is
cheap to catch.

### Resetting the database

```bash
rm src/Library.Api/library.db          # Windows: del src\Library.Api\library.db
dotnet run --project src/Library.Api   # recreated and reseeded
```

---

## Configuration

Settings are layered; later sources win.

1. `appsettings.json` — committed defaults
2. `appsettings.{Environment}.json` — per-environment overrides
3. User Secrets — local secrets, never committed (Phase 5)
4. Environment variables — `Database__Provider` (double underscore = nesting)
5. Command-line arguments

```json
"Database": {
  "Provider": "Sqlite",
  "ConnectionString": "Data Source=library.db",
  "EnableSensitiveDataLogging": false,
  "ApplyMigrationsOnStartup": false,
  "CommandTimeoutSeconds": 30
}
```

Bound to `DatabaseOptions` with `ValidateDataAnnotations().ValidateOnStart()`, so
a missing connection string fails the process at startup with a clear message
rather than surfacing as a `NullReferenceException` on the first request.

Full provider details are in [`database.md`](database.md).

---

## Troubleshooting

**`dotnet ef` says the startup project does not reference `Microsoft.EntityFrameworkCore.Design`**
You passed `--startup-project`. Don't — this project uses a design-time factory
so migrations run against `--project src/Library.Infrastructure` alone. Keeping
EF's build-time tooling out of the API project is deliberate.

**Build fails with `MSB3021: Unable to copy file ... being used by another process`**
The API is still running and holding the DLL. Stop it (Ctrl+C), or:
```powershell
Get-Process dotnet | Stop-Process -Force
```

**Build fails on an analyzer warning**
Expected. `TreatWarningsAsErrors` is on. Fix the code rather than suppressing the
rule — and if the rule genuinely does not apply (generated code, for instance),
scope the exclusion narrowly in `.editorconfig` and say why.

**`No LoggingLevelSwitch has been declared with name "..."` at startup**
Something added a `"//"` pseudo-comment key inside the Serilog `Override` block.
JSON has no comments, and Serilog treats **every** key there as a logger name.
Put the explanation in the docs instead. (This bit this project once; it is why
`appsettings.Development.json` has no inline comments.)

**Port 5112 already in use**
Change `applicationUrl` in `src/Library.Api/Properties/launchSettings.json`.

**HTTPS certificate warnings**
```bash
dotnet dev-certs https --trust
```

**Swagger UI is empty or 404**
It is registered in the Development environment only. Confirm with
`echo $ASPNETCORE_ENVIRONMENT` — it should be `Development`. The raw document is
always at `/openapi/v1.json`.

---

## Project layout

```
LibraryManagement.slnx              .slnx - the .NET 10 XML solution format
Directory.Build.props               net10.0, nullable, warnings-as-errors
Directory.Packages.props            central package versions (no versions in .csproj)
dotnet.config                       test runner selection
.editorconfig                       style + analyzer severities
.gitattributes                      line-ending normalisation

src/Library.Domain/                 no references, no packages
src/Library.Application/            -> Domain
src/Library.Infrastructure/         -> Application, Domain
src/Library.Api/                    -> Application, Infrastructure
tests/Library.UnitTests/            -> Domain, Application
tests/Library.IntegrationTests/     -> Api
```

Package versions live **only** in `Directory.Packages.props`. A `.csproj`
references a package by name:

```xml
<PackageReference Include="CsvHelper" />
```

Adding a version attribute there breaks central package management and the build
will tell you so.
