# Task Tracker

Working checklist for the Library Management API. Maintained by both the
developer and Claude Code.

**Legend:** `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked

**Definition of done:** an item is ticked only when the code works, it has been
verified by actually calling it, and its documentation exists. Compiling is not
done.

---

## Phase 1 — Boilerplate, Git, and Claude setup

- [x] Git repository initialised, `main` branch
- [x] `.gitignore` (bin, obj, `*.db`, `logs/`, `.claude/settings.local.json`)
- [x] `.gitattributes` — line-ending normalisation
- [x] Remote wired to GitHub and pushed
- [x] Solution + 6 projects, references enforcing the dependency rule
- [x] `Directory.Build.props` — net10.0, nullable, warnings-as-errors
- [x] `Directory.Packages.props` — central package version management
- [x] `.editorconfig`, with migrations excluded from analysis as generated code
- [x] Domain foundation — `Entity`, `AuditableEntity`, `IDomainEvent`
- [x] Exception hierarchy — `DomainException` + NotFound/Conflict/BusinessRule
- [x] `GlobalExceptionHandler` → RFC 9457 ProblemDetails with errorCode + traceId
- [x] Serilog, configuration-driven, with bootstrap logger for startup failures
- [x] Provider-agnostic `LibraryDbContext` + `DatabaseOptions` with `ValidateOnStart`
- [x] `IClock` / `SystemClock` — no direct clock reads in production code
- [x] OpenAPI document + Swagger UI (built-in generator, Swashbuckle UI only)
- [x] Health probes — `/health/live` (no DB) and `/health/ready` (DB)
- [x] Unit test foundation — `FakeClock`, entity identity, paging clamps (26 tests)
- [x] Integration test foundation — `LibraryApiFactory`, smoke tests (5 tests)
- [x] `CLAUDE.md`
- [x] `TASKS.md`
- [x] `.claude/skills/` — 8 skills (4 foundational, plus `csharp-standards-1rivet`,
      `xunit-testing`, `request-validation`, `domain-modelling`)
- [x] `.claude/commands/` — 4 slash commands
- [ ] `.github/workflows/ci.yml` — now unblocked: `dotnet test` works
- [ ] Graphify + Archify installed, first diagrams generated
- [x] `docs/phases/phase-01.md`

## Phase 2 — Book / Catalogue APIs

**Read side**

- [x] Entities — `Book`, `BookCopy`, `Author`, `Genre`, `Category`, `Publisher`
- [x] Junctions — `BookAuthor` (with credit order), `BookGenre`
- [x] `Isbn` value object with ISBN-13 check-digit validation
- [x] EF configurations — indexes, delete behaviours, backing fields
- [x] `InitialCatalogue` migration — 8 tables, 14 indexes
- [x] `DesignTimeDbContextFactory` — keeps EF tooling out of the API project
- [x] `DatabaseSeeder` — 15 books, 41 copies, via domain factories
- [x] `IBookRepository` + `BookRepository` — filter composition, SQL projection
- [x] `BookSortOptions` — sort whitelist (injection prevention)
- [x] `IBookService` + `BookService` — null → `NotFoundException`
- [x] `GET /api/books` — search, filter, sort, page
- [x] `GET /api/books/{id}`
- [x] `GET /api/books/isbn/{isbn}` — hyphenated or not
- [x] `GET /api/books/{id}/copies`

**Write side**

- [x] `POST /api/books` — 201, or 409 on duplicate ISBN
- [x] `PUT /api/books/{id}` — 200 / 404
- [x] `DELETE /api/books/{id}` — 204, or 409 when copies are on loan
- [x] `POST /api/books/{id}/copies` — 201, or 409 on duplicate barcode
- [x] `DELETE /api/copies/{id}` — 204
- [x] `PUT /api/copies/{id}` — 200, or 409 on duplicate barcode (re-labelling)
- [x] FluentValidation validators for every write request
- [x] Validation pipeline wiring — global `ValidationFilter`
- [x] `IUnitOfWork` — one transaction per use case; index violation -> 409
- [x] Strict JSON — unknown properties rejected with 400, not silently dropped
- [ ] Author / Genre / Category / Publisher lookup endpoints
- [ ] Unit tests for `BookService` write paths
- [ ] Integration tests for the book endpoints
- [x] `docs/phases/phase-02.md` (read side)
- [ ] `docs/api-contract.md` — **stale**: still lists the write endpoints as
      "planned", and does not cover the strict-JSON 400 or the editable barcode
- [ ] `docs/phases/phase-02.md` — extend for the write side

## Coding-standards compliance (1Rivet TEC-STD-005)

Gaps found when mapping the standard onto the codebase on 2026-09-13. Each was
verified against the current code, not assumed. Full rule-by-rule mapping lives
in the `csharp-standards-1rivet` skill.

- [ ] `DateTime.UtcNow` in `BookRequestValidators.cs:69,116,148` — breaks project
      rule 6 and makes the three "date not in the future" rules untestable.
      Inject `IClock` into the validators (the filter resolves them from DI)
- [ ] Empty `catch (UnauthorizedAccessException) { }` in `LibraryApiFactory.cs` —
      §7.4 forbids an empty catch; the `IOException` arm beside it is commented
- [ ] Brace-less `if` guards in `Entity.cs:41,42,46,50` — §6.1 requires braces
- [ ] `Book.Create` (9 params) and `Book.UpdateDetails` (8) exceed the §7.6 limit
      of 5. Introduce a `BookDetails` parameter record — also removes the
      argument-order hazard of eight consecutive nullables
- [ ] Max-length literals (`500`, `50`, `4000`) duplicated between the validators
      and the EF configurations, with nothing enforcing agreement. Hoist to shared
      constants
- [ ] `"^[A-Za-z0-9\\-_]+$"` in `BookRequestValidators.cs` should be a verbatim
      `@"..."` literal — §7.2
- [ ] No `<Version>` in `Directory.Build.props`, so every assembly ships as
      `1.0.0.0`
- [ ] Decide whether `file_header_template` + `IDE0073` (copyright headers) are
      required for this deliverable, or whether the recorded deviation stands

## Phase 3 — Reader / Member APIs

- [ ] `MembershipType` entity (max concurrent loans, loan period)
- [ ] `Member` entity with membership-number generation
- [ ] `Email` / `PhoneNumber` value objects
- [ ] Member CRUD + deactivate/reactivate
- [ ] `GET /api/members/{id}/loans`
- [ ] Uniqueness validated in app *and* enforced by index
- [ ] Tests + `docs/phases/phase-03.md`

## Phase 4 — Lending APIs (Loan + Fine)

- [ ] `Loan` entity — issue, return, overdue computed against `IClock`
- [ ] **Filtered unique index** `Loan(BookCopyId) WHERE ReturnedAt IS NULL`
- [ ] `Fine` entity — ₹50/day, via `FineRateResolver` delegate + `FineOptions`
- [ ] `LoanReturnedEvent` + post-commit domain event dispatcher
- [ ] C# `event` on the notification service (contrast with domain events)
- [ ] `POST /api/loans/issue` — 201, or **409 when the copy is already on loan**
- [ ] `POST /api/loans/{id}/return` — assesses fine when overdue
- [ ] `GET /api/loans` — filter by status/member/date range
- [ ] `POST /api/fines/{id}/pay`
- [ ] `DbUpdateException` → `409` translation for the index race
- [ ] Tests (incl. concurrent-issue race) + `docs/phases/phase-04.md`

## Phase 5 — Authentication & Authorization

- [ ] `User`, `Role`, `UserRole`, `RefreshToken` entities
- [ ] `Member.UserId` FK linking a member to their login
- [ ] Password hashing via `PasswordHasher<T>`
- [ ] JWT issuance; refresh-token rotation, stored **hashed**
- [ ] `POST /api/auth/register | login | refresh | logout`
- [ ] Roles `Librarian` / `Member`; policies on every endpoint
- [ ] `IAuthorizationHandler` for resource ownership (`GET /api/loans/me`)
- [ ] Swagger bearer security scheme
- [ ] Tests + `docs/phases/phase-05.md`

## Phase 6 — Import Books

- [ ] `POST /api/books/import` — streamed CSV/JSON upload
- [ ] Per-row validation with a row-level error report
- [ ] Dedupe strategy (skip / update / fail) — Strategy pattern
- [ ] Batched inserts in one transaction
- [ ] Upload limits: size, extension, content type
- [ ] `GET /api/books/import/template`
- [ ] Tests + `docs/phases/phase-06.md`

## Phase 7 — Reports & Export

- [ ] `GET /api/reports/books/export?format=csv|json` — streamed
- [ ] `GET /api/reports/loans?from=&to=&status=`
- [ ] `GET /api/reports/overdue?asOf=`
- [ ] `GET /api/reports/fines/summary`
- [ ] CSV formula-injection escaping (`=`, `+`, `-`, `@`)
- [ ] Aggregates computed in SQL, not in memory
- [ ] Tests + `docs/phases/phase-07.md`

## Phase 8 — Security & Vulnerability Hardening

- [ ] Secrets to User Secrets / environment variables (no JWT key in appsettings)
- [ ] Rate limiting on `/api/auth/*` (built-in `AddRateLimiter`)
- [ ] CORS tightened from the dev wildcard
- [ ] Security headers — HSTS, CSP, X-Content-Type-Options, Referrer-Policy
- [ ] Mass-assignment review — dedicated request DTOs everywhere
- [ ] `dotnet list package --vulnerable --include-transitive` in CI
- [ ] Audit logging for issue / return / login
- [ ] OWASP API Top 10 mapped to this codebase
- [ ] `docs/security.md` + `docs/phases/phase-08.md`

## Documentation

- [x] `CLAUDE.md`
- [x] `TASKS.md`
- [x] `README.md`
- [x] `docs/architecture.md`
- [x] `docs/data-model.md` — ERD + 1NF→3NF walkthrough
- [x] `docs/data-flow.md` — sequence diagrams
- [x] `docs/setup.md`
- [x] `docs/database.md`
- [~] `docs/api-contract.md` — read side documented; write side stale
- [ ] `docs/security.md`
- [~] `docs/phases/` — 01 and 02 written; 03-08 pending

## Stretch

- [ ] Phase 9 — Reservations (queue, hold expiry, `BackgroundService`)
- [ ] Phase 10 — CQRS (`Application/Features/*/Commands|Queries` + mediator)

---

## Open questions / known issues

| # | Item | Detail |
|---|---|---|
| 1 | SQL Server migrations not generated | Only the SQLite migration set exists. Needs SQL Server Express installed, then scaffold with `LIBRARY_DB_PROVIDER=SqlServer`. |
| 2 | Graphify / Archify not installed | Graphify needs Python 3.10+ (absent). Archify needs Node ≥ 22.19 (machine has 22.16). |
| 3 | Category cycle detection | `Category.MoveUnder` blocks direct self-parenting only. A longer cycle (A→B→A) needs an ancestor walk in the service layer. |

---

## Decisions log

| Date | Decision | Reasoning | Rejected alternative |
|---|---|---|---|
| 2026-09-12 | SQLite now, SQL Server later | No DB engine installed; provider chosen from config keeps the swap a config change | Installing SQL Server Express up front |
| 2026-09-12 | Clean Architecture, 4 projects | Dependency rule enforced by the compiler; CQRS drops into `Application/Features/` with no restructuring | N-tier (entities in the data layer — weaker DIP story) |
| 2026-09-12 | Custom JWT auth, `Member.UserId` link | Every table is one we designed and can defend in the normalisation discussion; enables resource-based authorization | ASP.NET Core Identity (7 tables we did not design) |
| 2026-09-12 | BookCopy split from Book | Loans must target a physical item so a library can lend one of three copies | One row per title (cannot express partial availability) |
| 2026-09-12 | Fine fixed at ₹50/day via delegate | `FineRateResolver` bound to `FineOptions` makes it configurable later without touching `Loan` | Hard-coded constant |
| 2026-09-12 | Reservations deferred to Phase 9 | Events requirement is already satisfied by the Fine flow; keeps the deliverable scoped | Building it in Phase 4 |
| 2026-09-12 | Controllers, not minimal APIs | Conventional REST, easier to narrate in an assessment | Minimal APIs |
| 2026-09-12 | `.slnx` solution format | .NET 10 default; cleaner XML, and .NET 10 already requires VS 17.14+ | Classic `.sln` |
| 2026-09-12 | xUnit v3 over the template's v2 | v2's `IAsyncLifetime.DisposeAsync()` returns `Task` and collides with `WebApplicationFactory`'s `IAsyncDisposable`; v3 uses `ValueTask` | xUnit v2 |
| 2026-09-12 | Built-in OpenAPI + Swashbuckle **UI only** | .NET 10 template dropped Swashbuckle generation; framework generator is AOT-friendly | `AddSwaggerGen` |
| 2026-09-12 | Built-in rate limiting | First-party since .NET 7; no third-party dependency | `AspNetCoreRateLimit` |
| 2026-09-12 | FluentValidation 12 core + DI extensions | `FluentValidation.AspNetCore` is deprecated — auto-validation was removed by the author | `FluentValidation.AspNetCore` 11.3.1 |
| 2026-09-12 | ISBN via private string backing field | An EF value converter is applied to *both* sides of a comparison, so `LIKE` breaks trying to convert `"%design%"` into an `Isbn`. A plain column keeps `LIKE` and index seeks working while the domain still exposes a validated value object. | `HasConversion` on the `Isbn` property |
| 2026-09-12 | Sort whitelist of expression trees | A parameter cannot stand in for a column name, so no escaping makes interpolation safe. The input is *looked up*, never used to build SQL. | Dynamic LINQ / string interpolation |
| 2026-09-12 | `Restrict` on Category FK, `Cascade` on copies | Deleting a category must not silently delete its books; a copy has no meaning without its title | Cascade everywhere |
| 2026-09-12 | Seeder class, not `HasData` | `HasData` bakes demo rows into migrations and needs hard-coded keys; the seeder also runs data through domain validation | `HasData` in configurations |
| 2026-09-12 | Strict JSON (`UnmappedMemberHandling.Disallow`) | System.Text.Json silently discards unknown members. Combined with narrow request DTOs that exist to prevent mass assignment, a caller could PUT a changed `barcode`, get 200, and reasonably believe it worked. The DTO was right to ignore it; the 200 was wrong. | Default lenient binding |
| 2026-09-12 | Barcode editable via the normal copy update | A damaged or unreadable label needs re-issuing, and delete-and-recreate would discard the copy's loan history. Uniqueness is checked excluding the row being edited, so an unchanged barcode does not self-conflict. | Immutable barcode; a dedicated /relabel endpoint |
| 2026-09-12 | `IUnitOfWork` over per-repository `SaveChanges` | Lets one use case span several mutations in a single transaction, and gives one place to translate a unique-index violation into a 409. Matches on provider ERROR NUMBERS, not message text, because messages are localised. | Committing inside each repository method |
| 2026-09-12 | `DesignTimeDbContextFactory` | Keeps `Microsoft.EntityFrameworkCore.Design` out of the API project — it is build tooling, not a hosting concern | Referencing Design from `Library.Api` |
| 2026-09-13 | Adopt TEC-STD-005 with a documented deviation register | The standard is from 2019 and targets .NET Framework. Mapping every rule to enforced / adapted / deviated / obsolete keeps compliance auditable and gives a reviewer a prepared answer instead of an argument | Silent partial compliance, or transcribing the standard without reconciling it |
| 2026-09-13 | Spaces, not tabs (§4.2) | `.editorconfig` already sets `indent_style = space`, `indent_size = 4`; `dotnet format` and the Roslyn defaults assume spaces, and spaces render identically everywhere. The standard's real goal — consistent 4-column indentation — is met | Tabs, as the standard literally specifies |
| 2026-09-13 | No `#region` blocks (§6.1) | Regions hide code from the reader while leaving it in the file, and collapse by default in most editors — which is how dead code survives review. The classes here are small enough not to need navigation aids | `#region` around interface implementations |
| 2026-09-13 | Keep the custom `DomainException` hierarchy (§7.4) | The error contract needs a stable machine-readable `ErrorCode` and one clean 4xx/5xx split. A built-in exception carries only prose, and mapping `InvalidOperationException` to 409 would report a genuine framework bug to the client as a business-rule failure | Built-in exception types, with or without a side-channel error code |
| 2026-09-13 | No `[Serializable]` / deserialization constructor on exceptions (§7.4) | `SerializationInfo`-based exception serialization is obsolete as of .NET 8 (SYSLIB0051) and `BinaryFormatter` is removed; with `TreatWarningsAsErrors` the pattern fails the build. Exceptions never cross a process boundary here — they become ProblemDetails JSON | Implementing the standard's full Exception Constructor Pattern |
| 2026-09-13 | Guard clauses over single-exit (§7.11) | The standard's own example trades three early returns for a mutable `isValid` local and deeper nesting. Single-exit is a C-era rule about resource cleanup, which `using`, `finally`, and the GC removed | One return per method |
| 2026-09-13 | Abstract base classes for `Entity` / `DomainException` (§7.7) | They carry state and behaviour — id, equality contract, domain-event list — which an interface cannot. Contracts *are* interfaces here (`IClock`, `IBookRepository`, `IUnitOfWork`, `IHasDomainEvents`) | Interfaces only, as the standard prefers |
| 2026-09-13 | Error messages inline, not in resources (§7.2) | `InvariantGlobalization` is on and the app is single-locale; the stable contract is `ErrorCode`, not the prose. Resources would add indirection with no reader today | `.resx` resource files for all message strings |
| 2026-09-13 | No per-file copyright header (§6.2) | Internal assessment repository with one licence at the root; `file_header_template = unset`. For client work, set the template and enable `IDE0073` so the header is generated and verified rather than copy-pasted | Hand-written copyright block in every file |
| 2026-09-13 | `global.json` selects Microsoft.Testing.Platform | The .NET 10 SDK retired the VSTest bridge that xUnit v3 does not use. `global.json` is the mechanism that works; a `dotnet.config` `[dotnet.test.runner]` section and `TestingPlatformDotnetTestSupport=true` both look equivalent and do nothing. `dotnet test` now runs 31 tests, unblocking CI. `dotnet.config` removed as dead config. | Continuing to run the test executables directly |
