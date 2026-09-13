# Phase 1 — Boilerplate, Git, and Tooling

**Goal:** a running, well-instrumented API host with nothing in it — so that
every later phase adds business value rather than plumbing.

---

## What we built

- Four-project Clean Architecture solution + two test projects
- Provider-agnostic EF Core registration
- Domain foundation: `Entity`, `AuditableEntity`, `IDomainEvent`, exception hierarchy
- Global exception handling → RFC 9457 ProblemDetails
- Serilog with source-generated logging
- OpenAPI + Swagger UI
- Liveness and readiness health probes
- 31 tests and the fixtures later phases build on

No endpoints. That is the point: the scaffolding is verified before anything
depends on it.

---

## Concepts

### 1. Clean Architecture and the dependency rule

**What it is.** Four layers, with source-code dependencies pointing *inward* only.
`Domain` at the centre depends on nothing; each outer layer depends only on those
inside it.

**Why here.** The system has genuine invariants — a copy cannot be issued twice,
fines derive from due dates — and those belong somewhere testable without a
database. Putting entities in `Library.Domain` with **no references and no NuGet
packages at all** means `Book.cs` contains only business rules, because there is
nothing else it *could* contain.

The payoff is that the compiler enforces it. A comment saying "don't reference EF
Core from the domain" is a comment. A project with no reference to EF Core cannot.

**The alternative we rejected.** Classic N-tier (`Api → Business → Data`) looks
similar but inverts one arrow: entities live in the data layer, so business rules
cannot compile without the ORM present. It is less ceremony, and it is the right
call for a thin CRUD app — but it makes the Dependency Inversion story much
weaker, and CQRS retrofits badly.

**What it costs.** More projects, more indirection, a real learning curve. For
three tables and no rules, overhead.

---

### 2. Dependency inversion, concretely

The principle is usually stated abstractly. Here is what it actually looks like:

```csharp
// Library.Application — declares what it needs
public interface IBookRepository { Task<BookDetailDto?> GetByIdAsync(int id, CancellationToken ct); }

// Library.Infrastructure — supplies it
public sealed class BookRepository : IBookRepository { /* EF Core */ }

// Library.Api — binds them at startup
services.AddScoped<IBookRepository, BookRepository>();
```

At **compile time** the arrow points inward: `Infrastructure` references
`Application`. At **runtime** it points outward: an Application service ends up
executing Infrastructure code.

That inversion is the whole trick, and it is why `Library.Application` can be
unit-tested with a substitute repository and no database.

---

### 3. Dependency injection lifetimes

| Lifetime | Meaning | Used for |
|---|---|---|
| Singleton | One for the process | `IClock` — stateless |
| Scoped | One per HTTP request | `DbContext`, repositories, services |
| Transient | One per resolution | Lightweight stateless helpers |

**The bug this prevents.** Registering a service as singleton when it depends on
`DbContext` is a *captive dependency*: the singleton captures one `DbContext` at
first resolution and reuses it across every concurrent request. `DbContext` is
explicitly not thread-safe, so the symptom is intermittent, load-dependent
corruption — the worst kind to diagnose.

The same reasoning explains the `CreateScope()` in the migration code:
`DbContext` is scoped, and resolving a scoped service from the root provider is
the same bug.

---

### 4. The options pattern

```csharp
services.AddOptions<DatabaseOptions>()
    .Bind(configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

**What it replaces.** `configuration["Database:Provider"]` scattered through the
codebase — a magic string no compiler checks, where a typo yields `null` at
runtime and there is nowhere to put validation.

**Why `ValidateOnStart` matters.** Without it, a missing connection string
surfaces as a `NullReferenceException` on the first request that touches the
database — possibly hours after deployment, in a stack trace that points nowhere
useful. With it, the process refuses to start and says exactly what is missing.

> Fail fast and loudly at startup. Never fail obscurely under load.

**Gotcha:** `ValidateDataAnnotations()` needs the
`Microsoft.Extensions.Options.DataAnnotations` package. The `[Required]`
attribute lives in the base library, but the validator that *reads* it ships
separately — so without the package the attributes compile and silently do
nothing.

---

### 5. Centralised exception handling

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
app.UseExceptionHandler();   // FIRST in the pipeline
```

**What it replaces.** A `try`/`catch` in every controller action. The duplication
is not the worst part — the worst part is that the one action where somebody
forgets returns a stack trace to the caller.

**Why `IExceptionHandler`, not middleware.** Before .NET 8 this was written as a
middleware component wrapping `next(context)`. `IExceptionHandler` is the
first-party replacement: handlers are tried in registration order, each returning
`true` if it handled the exception, and the framework owns the plumbing.

**The mapping is one `switch` expression.** Adding a domain exception means adding
one arm; forgetting produces a 500, which is loud and gets noticed — the right
failure mode for that mistake.

**The security control.** A `DomainException` carries a message written for the
caller, so its text is returned. Anything else is a bug, so the caller gets a
generic message plus a `traceId` while the real exception goes to the log.

> Pattern-matching order matters. `NotFoundException` must precede the
> `DomainException` catch-all, because C# takes the **first** match.

---

### 6. RFC 9457 Problem Details

A standard error shape, so clients parse one format rather than inventing a
convention per API.

`errorCode` is an extension this project adds: stable, machine-readable, and the
thing clients branch on. Without it, callers end up string-matching on prose —
which breaks the moment a message is reworded.

`UseStatusCodePages()` extends the same shape to framework-generated failures —
a 404 from an unmatched route, a 405 from the wrong verb — so there is genuinely
only one error format.

---

### 7. Source-generated logging

```csharp
[LoggerMessage(EventId = 5000, Level = LogLevel.Error,
    Message = "Unhandled exception on {Method} {Path}")]
private partial void LogUnhandledException(string method, string path, Exception ex);
```

**Why not `_logger.LogError("...", a, b)`.** That form boxes every value-type
argument and allocates a `params object[]` on **every** invocation — including
the ones where the level is disabled and the message is thrown away immediately.

`[LoggerMessage]` moves that to compile time: the generator emits a cached,
strongly-typed delegate that checks `IsEnabled` *before* touching the arguments.
The class must be `partial`, because the generator writes the method body.

Analyzer **CA1848** enforces this, and the build fails on warnings.

**Related gotcha — CA1873.** Even with the generated method, this still allocates:

```csharp
LogRequestFailed(status, code, httpContext.Request.Path, detail);
//                                             ^ PathString -> string conversion,
//                                               evaluated at the CALL SITE
```

Hoist it into a local first. The analyzer is pointing at something real.

---

### 8. Middleware order is behaviour

```csharp
app.UseExceptionHandler();       // FIRST — wraps everything below
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
// Phase 5: UseAuthentication() BEFORE UseAuthorization()
app.MapControllers();
```

Each component wraps those after it, so it only ever sees what the earlier ones
have already done. `UseExceptionHandler` is first because it must wrap
everything; anything registered before it throws into the void.

The auth ordering is the classic trap: reversed, authorization evaluates an
anonymous principal and every `[Authorize]` endpoint returns 401 regardless of
the token.

---

### 9. `IClock` — why not `DateTimeOffset.UtcNow`

One rule runs through the project: **no production code reads the system clock
directly.**

Overdue status and fines are pure functions of "what time is it now" versus the
due date. With the clock baked into the call stack, testing a 12-day-overdue loan
means waiting twelve days, or back-dating the loan and hoping the arithmetic is
symmetric — and it is that assumption, not the wait, that hides real bugs.

```csharp
var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
var loan  = Loan.Issue(copy, member, clock.UtcNow, loanPeriodDays: 14);
clock.AdvanceDays(26);          // now 12 days overdue
```

`DateTimeOffset` rather than `DateTime` because it carries an explicit UTC
offset and cannot be silently reinterpreted in another time zone.

> .NET 8 added `TimeProvider`, which serves the same purpose. A narrow
> domain-owned interface is used instead so `Application` depends on nothing it
> does not define — and because a two-member interface is easier to substitute
> than a class with a timer factory on it.

---

### 10. Liveness vs readiness

| Probe | Checks the database? | Question it answers |
|---|---|---|
| `/health/live` | **No** | Is the process alive? |
| `/health/ready` | Yes | Can it serve traffic? |

Liveness deliberately does not touch the database. An orchestrator restarting the
application because the database blipped turns a recoverable outage into an
outage **plus** a restart loop — the process was fine; its dependency was not.

---

### 11. Central package management

`Directory.Packages.props` holds every version; `.csproj` files reference by name:

```xml
<PackageReference Include="CsvHelper" />
```

In a multi-project solution it is otherwise trivially easy for two projects to
drift onto different versions of the same package — producing binding redirects,
"works on my machine" bugs, and a painful upgrade story.

---

### 12. Warnings as errors

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest-recommended</AnalysisLevel>
```

A warning you are allowed to ignore is a warning you *will* ignore. This caught
four real issues during Phase 1 alone — CA1000, CA1848, CA1873, CS1587.

**The escape hatch must be narrow.** EF Core's generated migrations trip these
rules, and the fix is scoped to the folder rather than disabling the rules
solution-wide:

```ini
[**/Persistence/Migrations/*.cs]
generated_code = true
dotnet_analyzer_diagnostic.severity = none
```

Strict on every hand-written file; silent on the ones a tool owns.

---

## Things that bit us

Worth reading — each cost real time.

### JSON has no comments

Startup crashed with:

```
No LoggingLevelSwitch has been declared with name
"Set to Information to see every SQL statement EF Core generates..."
```

The cause was a `"//"` pseudo-comment key inside Serilog's `Override` block.
Serilog treats **every key there** as a logger name. The `"//"` convention is
harmless in a section bound to a POCO that ignores unknown keys — and fatal in
one parsed as a dictionary.

**Lesson:** explanations belong in documentation, not in JSON keys.

### `dotnet test` on .NET 10 with xUnit v3 — *resolved*

```
Testing with VSTest target is no longer supported by
Microsoft.Testing.Platform on .NET 10 SDK and later.
```

The .NET 10 SDK retired the VSTest bridge; xUnit v3 targets Microsoft.Testing
Platform (MTP) instead, and something has to tell `dotnet test` which runner to
use.

**Two plausible-looking mechanisms that do not work.** A `dotnet.config` with a
`[dotnet.test.runner]` section, and the `TestingPlatformDotnetTestSupport=true`
MSBuild property. Both are documented in adjacent contexts, both appear to apply,
and neither changes the outcome — the SDK still routes through VSTest and fails.

**What actually works** is `global.json` at the repository root:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

plus `--solution` on the command line, which is now required:

```bash
dotnet test --solution LibraryManagement.slnx -c Release   # 31 passing
```

Passing the solution positionally is rejected: *"Specifying a solution for
'dotnet test' should be via '--solution'"*.

**Lesson:** when three mechanisms look equivalent, the one that works is a fact
to be established by running it, not inferred from documentation. This blocked
CI for two phases on the assumption that it was unresolvable.

### The `.NET 10` template no longer ships Swashbuckle

From .NET 9 the `webapi` template dropped it. Document generation moved into the
framework (`AddOpenApi` / `MapOpenApi`), which is trimming- and AOT-friendly —
but the framework ships **no UI**.

The resolution: framework generator produces `/openapi/v1.json`, and
`Swashbuckle.AspNetCore.SwaggerUI` — the UI package alone — renders it. There is
no `AddSwaggerGen` call anywhere in this solution.

Related: `Microsoft.OpenApi` 2.x flattened its namespaces. `OpenApiInfo` now
lives in `Microsoft.OpenApi`, not `Microsoft.OpenApi.Models`.

### xUnit v2 collides with `WebApplicationFactory`

v2's `IAsyncLifetime.DisposeAsync()` returns `Task`, which clashes with the
`IAsyncDisposable.DisposeAsync()` (`ValueTask`) that `WebApplicationFactory`
already implements — `CS0738`.

v3's `IAsyncLifetime` extends `IAsyncDisposable` and uses `ValueTask`, so it
composes cleanly. That, not novelty, is why this project uses xUnit v3 despite
the template scaffolding v2.

---

## SOLID in this phase

| Principle | Where |
|---|---|
| **S** | `GlobalExceptionHandler` does one thing; `SystemClock` does one thing |
| **O** | The exception→status `switch` extends by adding an arm |
| **L** | `IClock` → `SystemClock` / `FakeClock`, indistinguishable to callers |
| **I** | `IClock` has two members, not a general-purpose time API |
| **D** | Application declares `IClock`; Infrastructure implements it |

---

## Verification

```bash
dotnet build LibraryManagement.slnx -c Release   # 0 warnings, 0 errors
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe
./tests/Library.IntegrationTests/bin/Release/net10.0/Library.IntegrationTests.exe
dotnet run --project src/Library.Api
```

```bash
curl http://localhost:5112/health/live      # Healthy
curl http://localhost:5112/health/ready     # Healthy
curl http://localhost:5112/api/nope         # 404 ProblemDetails, not an empty body
```

---

## Questions you should be able to answer

1. Why does `Library.Domain` have no NuGet packages? What breaks if you add one?
2. Compile time vs runtime: which way does the dependency arrow point, and why both?
3. What is a captive dependency, and why is the symptom intermittent?
4. Why must `UseExceptionHandler` come first?
5. Why does liveness deliberately not check the database?
6. What does `[LoggerMessage]` avoid that `_logger.LogError("...", a, b)` does not?
7. Why inject `IClock` instead of calling `DateTimeOffset.UtcNow`?
8. Why `ValidateOnStart()` rather than validating on first use?
9. Why is SQLite used for integration tests rather than EF Core's InMemory provider?

---

## What Phase 2 builds on this

- Entities inherit `AuditableEntity`, so timestamps are stamped centrally.
- `NotFoundException` becomes a 404 with no controller code.
- `IBookRepository` follows the `IClock` pattern: declared in Application,
  implemented in Infrastructure.
- `LibraryApiFactory` gains its first real endpoint tests.
- The provider-agnostic `DbContext` gets its first entities.
