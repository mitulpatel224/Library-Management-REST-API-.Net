# CLAUDE.md

Instructions for Claude Code when working in this repository.

## What this project is

A Library Management REST API built as a .NET assessment deliverable. A librarian
manages a book catalogue, members, and lending. The system must prevent issuing a
copy that is already on loan, and compute overdue status and fines from the due date.

**The point of the exercise is explaining the decisions, not shipping features.**
Every phase produces working code *and* a concept guide in `docs/phases/`. Code
without its explanation is an incomplete deliverable here.

## Stack

| | |
|---|---|
| Runtime | .NET 10 (`net10.0`), C# 14 |
| API style | Attribute-routed **controllers** (not minimal APIs) |
| ORM | EF Core 10 |
| Database | SQLite in development; SQL Server profile ready |
| Validation | FluentValidation 12 (`FluentValidation.AspNetCore` is deprecated — do not add it) |
| Logging | Serilog |
| Tests | xUnit **v3** + NSubstitute + Shouldly |
| OpenAPI | Built-in `AddOpenApi()`; Swashbuckle for the **UI only** |

## Architecture rules

Four projects. Dependencies point inward, enforced by project references:

```
Library.Domain          (no references at all)
    ^
Library.Application     -> Domain
    ^
Library.Infrastructure  -> Application, Domain
    ^
Library.Api             -> Application, Infrastructure
```

**Non-negotiable:**

1. **`Library.Domain` references nothing.** No EF Core, no ASP.NET Core, no NuGet
   packages. If something seems to need one, define an interface in Application
   and implement it in Infrastructure.
2. **Never put EF Core attributes on domain entities.** All mapping goes in
   `IEntityTypeConfiguration<T>` classes under
   `Infrastructure/Persistence/Configurations/`.
3. **`IQueryable` never escapes the repository.** Repositories return
   materialised DTOs or `PagedResult<T>`.
4. **Controllers contain no business logic and no try/catch.**
   `GlobalExceptionHandler` maps exceptions to status codes centrally.
5. **No provider-specific SQL outside Infrastructure.** It is what keeps the
   SQL Server swap a config change.
6. **Never call `DateTime.Now` or `DateTimeOffset.UtcNow` in production code.**
   Inject `IClock`. Only `SystemClock` reads the real time. Overdue and fine
   logic depends on this being testable.
7. **Never interpolate user input into a query.** Sorting resolves through the
   whitelist in `BookSortOptions.SortMap`. A parameter cannot stand in for a
   column name, so there is no safe escaping alternative.

## Conventions

- `sealed` by default on classes not designed for inheritance.
- `record` for DTOs and domain events; `class` for entities.
- Entities: private constructor for EF, `private set` properties, static factory
  method that validates. Collections exposed as `IReadOnlyCollection<T>` over a
  private backing list, mutated only through entity methods.
- `CancellationToken` is the **last parameter** on every async method and is
  threaded all the way to the database call.
- Async methods end in `Async`.
- Domain exceptions carry a stable snake-case `ErrorCode` (`book.not_found`,
  `loan.copy_already_on_loan`). Clients branch on the code, humans read the message.
- Logging uses `[LoggerMessage]` source-generated partial methods, not
  `_logger.LogX("...", args)` — analyzer CA1848 enforces this, and the build
  fails on warnings.

## Build and verify

```powershell
dotnet build LibraryManagement.slnx -c Release   # must be 0 warnings
dotnet run --project src/Library.Api             # http://localhost:5112/swagger
```

**Warnings are errors.** Do not suppress an analyzer to make a build pass —
either fix the code or, if the rule genuinely does not apply (e.g. generated
code), scope the exclusion narrowly in `.editorconfig` and say why.

Tests:

```powershell
dotnet test --solution LibraryManagement.slnx -c Release
```

`--solution` is required — passing the solution positionally is rejected by the
new runner. It works because `global.json` selects Microsoft.Testing.Platform,
which is what xUnit v3 targets now that the .NET 10 SDK has retired the VSTest
bridge. A single suite can also be run directly, since MTP builds each test
project as an executable:

```powershell
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe
```

Migrations run against Infrastructure alone, via `DesignTimeDbContextFactory`:

```powershell
dotnet ef migrations add <Name> --project src/Library.Infrastructure --context LibraryDbContext --output-dir Persistence/Migrations
```

## Working agreement

- **Ask before starting each new API or phase.** The user validates incrementally
  and wants to understand each change before the next one lands.
- **Update `TASKS.md` as part of the work**, not afterwards. Tick an item only
  when code *and* docs for it are done. Add discovered work as new items rather
  than silently widening one.
- **Record every architecture decision** in the Decisions Log at the bottom of
  `TASKS.md`, with the date and the reasoning — including the option rejected.
- **Verify before claiming.** Run the endpoint, read the response. Do not report
  something as working on the strength of it having compiled.
- Regenerate `docs/diagrams/` at the end of a phase (`/graphify`, `/archify`)
  once those tools are installed.

## Phases

| Phase | Scope | State |
|---|---|---|
| 1 | Boilerplate, git, Claude setup | Done |
| 2 | Book APIs | Reads and writes done; lookup endpoints, tests and phase doc outstanding |
| 3 | Reader / Member APIs | Done |
| 4 | Lending APIs (Loan + Fine) | Done |
| 5 | Authentication & authorization | **Deferred** — 6 and 7 come first |
| 6 | Import books | Next |
| 7 | Reports & export | After 6 |
| 8 | Security & vulnerability hardening | Not started |
| 9 | Reservations | Stretch |
| 10 | CQRS refactor | Stretch |

Fines are fixed at **₹50 per overdue day**, supplied through a `FineRateResolver`
delegate bound to configuration, so making it librarian-configurable later is a
data change rather than a code change.
