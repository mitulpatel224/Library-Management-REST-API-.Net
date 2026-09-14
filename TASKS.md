# Task Tracker

Working checklist for the Library Management API. Maintained by both the
developer and Claude Code.

**Legend:** `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked

**Definition of done:** an item is ticked only when the code works, it has been
verified by actually calling it, and its documentation exists. Compiling is not
done.

---

## Where this stands — 2026-09-14

| | |
|---|---|
| Phases complete | 1, 3, 4, 6 · Phase 2 write side shipped, lookups and tests outstanding |
| Next | 7 (reports & export). **Phase 5 (auth) deliberately deferred** |
| Endpoints | 35 controller actions (books, copies, members, membership types, loans, fines, import) + 2 health probes |
| Tests | 160 unit + 5 integration, all passing |
| Build | Release, 0 warnings (warnings are errors) |
| Branch | `main` |

**Phases 6 and 7 do not depend on Phase 5.** Import and export are both streamed
endpoints over data that already exists. The only consequence of deferring auth is
that both ship unauthenticated, which Phase 8 revisits regardless.

**CI has been triggered for the first time** by the push of `03fd4c6` — the
workflow was added in `2b17b99` and had never run before that. Its result is
unverified from this machine (`gh` is not installed); check
[Actions](https://github.com/mitulpatel224/Library-Management-REST-API-.Net/actions).

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
- [x] `.github/workflows/ci.yml` — build + test + vulnerability scan
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

- [x] `DateTime.UtcNow` in `BookRequestValidators.cs` — `IClock` injected into
      the three validators; 25 tests added, including one that proves the rule
      follows the clock rather than the machine date
- [ ] Empty `catch (UnauthorizedAccessException) { }` in `LibraryApiFactory.cs` —
      §7.4 forbids an empty catch; the `IOException` arm beside it is commented
- [ ] Brace-less `if` guards in `Entity.cs:41,42,46,50` — §6.1 requires braces
- [ ] `Book.Create` (9 params) and `Book.UpdateDetails` (8) exceed the §7.6 limit
      of 5. Introduce a `BookDetails` parameter record — also removes the
      argument-order hazard of eight consecutive nullables
- [ ] Max-length literals (`500`, `50`, `4000`) duplicated between the validators
      and the EF configurations, with nothing enforcing agreement. Hoist to shared
      constants
- [x] Barcode regex is now a verbatim `@"..."` literal — §7.2
- [ ] No `<Version>` in `Directory.Build.props`, so every assembly ships as
      `1.0.0.0`
- [ ] Decide whether `file_header_template` + `IDE0073` (copyright headers) are
      required for this deliverable, or whether the recorded deviation stands

## Phase 3 — Reader / Member APIs

- [x] `MembershipType` entity (max concurrent loans, loan period)
- [x] `Member` entity with membership-number generation
- [x] `Email` / `PhoneNumber` value objects
- [x] Member CRUD + suspend/reactivate/expire/cancel
- [x] Uniqueness validated in app *and* enforced by index
- [x] `GET /api/membership-types`, `/{id}`, `POST` — types surface
- [x] `docs/phases/phase-03.md`
- [x] `GET /api/members/{id}/loans` — deferred to Phase 4 and **delivered there**,
      once a `Loan` entity existed for it to return. `/balance` added alongside it
- [x] **Tests for members** — delivered in Phase 4 (`MemberTests`,
      `ValueObjectTests`). Closed issue 4

## Phase 4 — Lending APIs (Loan + Fine)

- [x] `Loan` entity — issue, return, renew, overdue computed against `IClock`
- [x] **Filtered unique index** `Loan(BookCopyId) WHERE ReturnedAt IS NULL`
      — provider-aware, applied in `OnModelCreating`
- [x] `Fine` entity — ₹50/day, via `FineRateResolver` delegate + `FineOptions`
- [x] `LoanReturnedEvent` + post-commit domain event dispatcher
- [x] C# `event` on the notification service (contrast with domain events)
- [x] `POST /api/loans/issue` — 201, or **409 when the copy is already on loan**
- [x] `POST /api/loans/{id}/return` — assesses fine when overdue
- [x] `POST /api/loans/{id}/renew` — refused once overdue
- [x] `GET /api/loans` — filter by status/member/date range
- [x] `GET /api/members/{id}/loans` — deferred in from Phase 3
- [x] `GET /api/members/{id}/balance`
- [x] `POST /api/fines/{id}/pay`, `/waive`, `GET /api/fines`, `/{id}`
- [x] `DbUpdateException` → `409` translation for the index race
- [x] Member + loan + fine test suites (56 → 147 tests)
- [x] `docs/phases/phase-04.md`
- [ ] **Automated concurrent-issue race test** — the race is proven by hand and
      the partial index by the re-issue path, but no test fires two concurrent
      issues. See issue 9.

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

- [x] `POST /api/books/import` — streamed CSV/JSON upload
- [x] Per-row validation with a row-level error report
- [x] Dedupe strategy (skip / update / fail) — Strategy pattern
- [x] Batched inserts (100 rows), two saves per batch for the join keys
- [x] Upload limits: 20 MB, extension allow-list, multipart-only
- [x] `GET /api/books/import/template`
- [x] Reader unit tests (17)
- [x] Lookup resolution by name, creating absent rows
- [ ] Integration test for the import endpoint
- [ ] `docs/phases/phase-06.md`

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
- [~] `docs/api-contract.md` — books read side, all member endpoints, and all
      loan/fine endpoints documented with real captured responses; **book write
      side still stale**
- [ ] `docs/security.md`
- [~] `docs/phases/` — 01–04 written; 05–08 pending

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
| 4 | ~~No automated tests for members~~ **CLOSED in Phase 4** | Member, value-object, loan and fine suites added: 56 → 147 tests. `MemberTests`, `ValueObjectTests`, `LoanTests`, `FineTests`, `FineAssessmentHandlerTests`. |
| 5 | `MembershipTypes.Name` uniqueness is not race-proof | The repository compares `UPPER()` on both sides, which makes the API behave identically on both providers, but a concurrent insert can still slip two casings past a SQLite `BINARY` index. Needs `COLLATE NOCASE` on the column, which makes the migration model provider-specific. |
| 6 | No audit trail on member status changes | `StatusReason` records *why* but nothing records *who* or *when*. Deferred to Phase 5, when there is an authenticated identity to attribute it to. |
| 7 | Membership expiry cannot be automated | `MembershipType` carries no duration, so nothing can compute when a membership lapses. `POST /api/members/{id}/expire` exists for a librarian to call explicitly. Adding a duration is a modelling decision, deliberately not guessed. |
| 8 | `BookSearchRequest` date ranges unvalidated | `MemberSearchRequest` and `LoanSearchRequest` now refuse inverted ranges. The book search very likely has the same gap; unprobed. |
| 9 | No automated concurrent-issue race test | Two members racing the same copy was verified by hand (exactly one 201, one 409, twice), and the partial index proven by returning then re-issuing a copy. Neither is a regression test. Needs an integration test firing concurrent issues against a shared database. |
| 10 | Unpaid fines do not block borrowing | `Member.CanBorrow` is still `Status == Active`. `GET /api/members/{id}/balance` reports the debt but nothing acts on it. Whether an outstanding fine should stop a loan is a policy decision, deliberately not invented. |
| 11 | No partial fine payment | `POST /api/fines/{id}/pay` settles in full. Part payment needs an amount-paid column, an overpayment rule, and a decision about whether a partly-paid fine still blocks borrowing. |
| 12 | Failed fine assessment is logged, not retried | Domain event handlers run after the commit and their failures are isolated by design — but a fine that fails to write needs someone to read the log. No retry or outbox. |

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
| 2026-09-13 | `IClock` injected into FluentValidation validators | The three "date cannot be in the future" rules compared against `DateTime.UtcNow`, so the only way to test them was to change the machine's date — meaning they were never tested. Validators are resolved from DI by `ValidationFilter`, so constructor injection works exactly as in a service. | Leaving the direct clock read and testing the rules manually |
| 2026-09-13 | CI fails the build on a vulnerable package, warns on a deprecated one | A CVE arriving through a transitive dependency is the likely case and should stop a merge. Deprecation often has no available replacement, so failing on it would force either a rushed migration or a permanently red pipeline. `dotnet list package --vulnerable` exits 0 even on a finding, so the step greps the output rather than trusting the exit code. | Failing on both, or trusting the exit code |
| 2026-09-13 | Membership type name uniqueness compared via `UPPER()` on both sides | The check was `t.Name == name.Trim()`, which the unique index folds case for on SQL Server (default CI collation) but not on SQLite (BINARY). Browser testing confirmed the split: `"Reference Only"` returned 409 while `"reference only"` returned **201** and created a second row — data that would then fail the unique index on a SQL Server migration. `UPPER()` translates on both providers, so the verdict no longer depends on which one is configured. Note the residual gap: SQLite's `UPPER()` folds ASCII only, and the app-level check still cannot stop a race between two concurrent inserts. | A provider-specific `COLLATE NOCASE` on the column (migrations must then carry a per-provider model), or storing a normalised shadow column as `Email` does — rejected because the display casing of a type name has to survive |
| 2026-09-13 | CA1862 suppressed by `#pragma` on that one statement | The analyzer's fix — `string.Equals(.., StringComparison.OrdinalIgnoreCase)` — is right for in-memory code and wrong inside an EF Core expression tree: EF cannot translate the `StringComparison` overloads, so taking the advice trades a build error for a runtime "could not be translated" exception. Scoped to the single statement, not the file or `.editorconfig`, so the rule keeps protecting every ordinary comparison around it. | An `.editorconfig` entry (too broad — would silence the rule across the whole repository or file), or `EF.Functions.Like` (avoids the analyzer but makes `%` and `_` in a type name behave as wildcards) |
| 2026-09-13 | Re-cancelling a cancelled membership returns 409, not a silent 200 | Browser testing showed a second `POST /cancel` returned 200 and overwrote `StatusReason` — the closure reason, which may document a data protection request, was rewritable by any caller with no history. A no-op returning 200 was rejected for the same reason `UnmappedMemberHandling.Disallow` exists: the call carries a new reason, and answering 200 while discarding it reports a revision that did not happen. Guard lives in `Member.Cancel()` so the seeder and any future import path are bound by it too, not just the HTTP path. New code `member.already_cancelled` rather than reusing `member.cancelled`, because the caller's remedy differs — nothing to do, versus cannot do. | Idempotent no-op returning the existing record (mirrors `Reactivate()`, but that takes no payload so there is no caller intent to discard); preserving the first reason and returning 200 (same 200-while-ignoring-input trap) |
| 2026-09-13 | `Suspend()` still allows its reason to be revised | Deliberate asymmetry with `Cancel()`, not an oversight. A suspension is reversible and ongoing, so its grounds can legitimately be updated as fines accumulate; a cancellation is terminal and its reason is a historical record. Different lifetimes, different rules. | Making both strict for surface consistency |
| 2026-09-13 | Model-binding and framework failures get the API's own ProblemDetails shape | `GlobalExceptionHandler` only sees thrown exceptions, so malformed JSON, a wrong type, an unknown property, a bad enum in the query string, 415 and 405 all fell through to ASP.NET Core's default 400 — a second error format with no `errorCode`. The contract says clients branch on the code, so an entire family of responses was unbranchable, and invisible until someone wrote a client. `InvalidModelStateResponseFactory` plus `CustomizeProblemDetails` (new `RequestProblemDetails`) close it. `Customize` only fills fields that are absent, so a domain code like `member.not_found` is never overwritten by a generic one. | Leaving the framework default (two formats), or a middleware rewriting responses after the fact (parsing our own JSON back out to patch it) |
| 2026-09-13 | Binding error messages rewritten, not passed through | System.Text.Json names .NET types in its messages — `"could not be mapped to any .NET member contained in type 'Library.Application.Members.Requests.CreateMemberRequest'"` — handing a caller the assembly layout, namespaces and DTO names. `GlobalExceptionHandler` already refuses to leak exception text; these bypassed it. Messages are now rewritten to say what the caller can act on and nothing about the server, with a regex backstop replacing any message still naming a `System.`/`Microsoft.`/`Library.` type. Query and route messages ("The value 'NotAStatus' is not valid for Status.") pass through — they already name only the value and the field. | Suppressing detail entirely (a bare 400 tells the caller nothing), or only sanitising in Production (leaves the dev-prod behaviour split that hides the problem until deploy) |
| 2026-09-13 | Enums serialise as names | `"status": 0` is unreadable without the enum definition beside it, and a client branching on the ordinal breaks silently the day a value is inserted in the middle. `MemberStatus` already numbers itself explicitly to survive that — which protected the database only; this protects the API. Reads stay permissive: `JsonStringEnumConverter` accepts both the name and the number, so existing callers sending `0` keep working. | Leaving ordinals (contradicts the stated "branch on stable strings" contract), or a per-property converter attribute (the domain must not carry serialisation attributes) |
| 2026-09-13 | `joinedOn` upper bound is `clock.Today`, lower bound 1900-01-01 | The rule read `LessThanOrEqualTo(clock.Today.AddDays(1))` while its message said "cannot be in the future", so tomorrow was accepted — the rule and its explanation disagreed and the message was the one stating the intent. Browser testing also issued `MEM-1800-00014`: the join year is baked into an immutable membership number, so a mis-keyed century is permanent. The floor is enforced in `Member.Create` as well as the validator, so the seeder and any future import path are bound by it; the upper bound stays validator-only because the entity has no clock and reading one would be the `DateTime.Now` this codebase forbids. | A relative floor (e.g. 120 years ago — moves under existing rows), or validator-only (leaves non-HTTP paths free to issue MEM-1800) |
| 2026-09-13 | An inverted `joinedFrom`/`joinedTo` range is refused, while page and sort are still clamped | Deliberately inconsistent with the rest of the query string. Page size and an unknown sort field clamp because there is a sensible value to clamp to and the caller still gets a useful answer. An inverted range has none — it returned an empty page indistinguishable from "nobody joined in that window", so the caller reads a real answer to a question they did not ask. | Clamping by swapping the two dates (silently answering a different question), or leaving it (the empty page is misleading, not merely unhelpful) |
| 2026-09-13 | `POST /api/membership-types` Location points at the created type; `GET /api/membership-types/{id}` added | `CreatedAtRoute("GetMembershipTypes", null, ...)` returned the collection URL, so a 201 handed the client the whole list and left it to find the row it had just made — the one job the header exists to save it. The by-id endpoint had to exist first. | Leaving the collection URL, or dropping the Location header (a 201 without one is worse) |
| 2026-09-13 | `POST /api/members/{id}/expire` exposed; `Member.Expire()` now refuses a cancelled membership | `MemberStatus.Expired` and `Member.Expire()` both existed with nothing able to reach them — a persisted state the API could not produce. `Expire()` also returned silently for a cancelled member, which would have answered 200 to a librarian whose click did nothing; Suspend and Reactivate both throw for that case, so this now matches. Automatic expiry is NOT implemented: `MembershipType` carries no duration, so there is no data to drive it. That is a modelling decision, not an oversight to fix here. | Removing `Expire()` and `Expired` (loses a persisted enum value and the lapsed-vs-suspended distinction), or inventing a membership duration to drive automatic expiry |
| 2026-09-13 | `NotFoundException` converts PascalCase resource names to snake_case | `resource.ToLowerInvariant()` produced `membershiptype.not_found` sitting beside `membership_type.duplicate_name` — two conventions in one API, where the contract promises one. Surfaced only when the first multi-word resource was added; every existing single-word code is unchanged. | Passing `"membership_type"` as the resource name (the message would then read "membership_type with identifier '9' was not found") |
| 2026-09-13 | Duplicate-name conflicts quote the stored spelling | `FindMembershipTypeNameAsync` returns the name as stored rather than a bool, so the 409 reads "A membership type named 'Standard' already exists" for a caller who sent `sTaNdArD`. Echoing the caller's casing sends a librarian looking for a type that is not on file under that name. | Keeping the bool and echoing `request.Name` |
| 2026-09-13 | The lending invariant is enforced by a filtered unique index, not by the service check | `UX_Loans_BookCopyId_Active ... WHERE ReturnedAt IS NULL`. `BookCopy.MarkOnLoan()` and `LoanService` both check for an existing loan, and both READ before the insert WRITES — two concurrent requests pass both. The index is the only participant that serialises the writes. The filter is what makes it expressible at all: `Loan(BookCopyId)` cannot be unique outright because a copy is lent hundreds of times. Verified by racing two members for one copy (exactly one 201, one 409) and by returning then re-issuing a copy, which an unfiltered index would reject. | Service-level locking (does not survive more than one process), or a `SELECT ... FOR UPDATE` style pessimistic lock (SQLite has no row locking, so it would not port) |
| 2026-09-13 | `HasFilter` SQL is applied per provider in `OnModelCreating` | Both providers implement the feature — SQL Server "filtered index", SQLite "partial index" — but `HasFilter` takes RAW SQL that EF Core emits verbatim, identifier quoting included. `[ReturnedAt] IS NULL` would put SQL Server brackets into a SQLite migration. An unrecognised provider throws at startup rather than emitting an index with no filter, which would be unique across ALL loans for a copy and reject the second loan of every book. Second time in two phases that "the provider swap is a config change" needed active work. | Hard-coding one provider's syntax in `LoanConfiguration`; relying on ANSI double quotes working on both (true in practice, but only while `QUOTED_IDENTIFIER` stays ON) |
| 2026-09-13 | SQLite stores `DateTimeOffset` as UTC `DateTime` via a provider-scoped value converter | `GET /api/loans` returned 500: `SqliteQueryableMethodTranslatingExpressionVisitor.TranslateOrderBy` throws `NotSupportedException`. SQLite has no date type, so the provider writes `DateTimeOffset` as TEXT with the offset appended — text order is not chronological order, and EF Core refuses to translate `ORDER BY` rather than return a wrong answer. Converting to UTC loses nothing (every timestamp comes from `IClock.UtcNow`, so the offset is always zero) and yields a stored form whose lexicographic order IS chronological. SQL Server untouched: `datetimeoffset` is a real type there. Worth remembering: a working `WHERE` on a column does not mean `ORDER BY` will work on it. | Storing ticks as `long` (sortable but unreadable in any raw query or report); changing the domain to `DateTime` (loses the offset on SQL Server for no gain) |
| 2026-09-13 | Fines are assessed by a post-commit domain event handler, not inline in `ReturnAsync` | A C# `event` fires inline, before the transaction commits — a rollback would leave a fine assessed for a return that never happened, with no record it was wrong. So `Loan.Return()` only COLLECTS `LoanReturnedEvent` and the dispatcher runs handlers once `SaveChangesAsync` succeeds. The cost is explicit: the fine is written in a separate transaction, so there is a window with the copy back and no fine, and a failing handler leaves it open. A late fine beats a lost return. | Assessing inline in the same transaction (atomic, but makes the event mechanism decorative and lets a fine-assessment failure roll back a return that physically happened at the desk) |
| 2026-09-13 | A C# `event` is kept for notifications, deliberately alongside domain events | Not duplication — the two differ on when they run, what happens on rollback, how subscribers are wired, and how failures propagate. A notification is advisory: it writes nothing, and nobody is harmed if a subscriber misses it, so firing inline is correct. A fine is a financial record, so it gets the mechanism with transactional ordering. The contrast is the teaching point. | Using domain events for both (over-engineers an advisory notification); using a C# event for both (assesses fines that may be rolled back) |
| 2026-09-13 | `LoanStatus` is computed at read time, never stored | A loan becomes overdue at midnight with nothing writing to its row, so a column would need a nightly job and would be wrong between the due date and the next run. Filtering still happens in SQL — the three states are date predicates — because materialising every loan to discard the non-overdue ones would defeat paging. | A stored column plus a scheduled job (a stale row between runs, and a second thing to operate) |
| 2026-09-13 | Days overdue truncates; days remaining rounds up | Opposite rounding on purpose. A copy an hour late is not yet a day late, and charging ₹50 for it is indefensible at the desk. Conversely a loan issued moments ago for 14 days has 13.999 days left, and truncating told the member they had 13 when the librarian promised 14. Each direction favours the member, which is the answer that can be explained. | Consistent rounding in one direction (defensible as a rule, wrong in one of the two conversations) |
| 2026-09-13 | `Loan.Return()` throws when `BookCopy` is not loaded, rather than using `?.` | Found by two failing unit tests. `BookCopy?.MarkReturned(condition)` silently closed the loan while leaving the copy `OnLoan` — and because the filtered index constrains only OPEN loans, nothing would ever point at the stranded copy; it would simply never be lendable again. `Loan.Issue` now sets the navigation as well as the id, and `Return` fails loudly. `?.` is a null check that looks like a safety feature and was suppressing the signal that mattered. | Keeping the null-conditional (silent data corruption); loading the copy defensively inside the entity (the domain cannot reach a repository) |
| 2026-09-13 | Overdue loans are seeded, relative to `IClock` | Overdue behaviour cannot be reached by calling the API and waiting — a fresh loan is not late for a fortnight — so the overdue report and the whole fine path were unverifiable by hand and undemonstrable. Seeded loans 16 and 61 days overdue are computed from the clock, so they stay overdue whenever the database is rebuilt; a hard-coded date would be right on the day it was written and wrong every day after. | Only testing overdue behaviour in unit tests with `FakeClock` (leaves the HTTP path unexercised); hard-coded seed dates (rot immediately) |
| 2026-09-13 | Fine payment is all-or-nothing | Part payment needs an amount-paid column, a rule for overpayment, and a decision about whether a partly-paid fine still blocks borrowing. Accepting an amount without those would half-answer all three. Recorded as issue 11 rather than built. | Accepting an arbitrary amount now and deciding the rest later |
| 2026-09-14 | Import creates missing lookups rather than rejecting the row | A supplier's file carries names, not ids, and cannot pre-register anything. Rejecting every row whose publisher is unknown would fail the entire first import from any new supplier — exactly when the feature is needed. The cost is that a typo creates a lookup row, mitigated by case-insensitive matching and by reporting `lookupsCreated` so an unexpected number is visible. | Requiring every lookup to exist beforehand |
| 2026-09-14 | Import reports partial success as 200, not 4xx | The unit of failure is a row, not the request. A file with 9,996 good rows and 4 bad ones should import the 9,996 and name the 4 with their line numbers. A 4xx would claim the upload was wrong when only part of it was. | Failing the whole file on any bad row |
| 2026-09-14 | JSON import is lenient about unknown properties; the API is strict | Opposite settings for opposite situations. The API owns both ends of its contract, so an unrecognised field means the caller misunderstood and should be told. An import file comes from a supplier with their own schema, where extra fields are expected and refusing over one we do not need makes the feature unusable. | One global JSON policy |
| 2026-09-14 | Malformed JSON imports nothing; malformed CSV imports the good rows | Not a design choice so much as a consequence worth documenting. `DeserializeAsyncEnumerable` buffers ahead, so a syntax error surfaces before earlier valid elements are yielded. CSV is line-oriented and recovers. Both behaviours are asserted by tests so neither changes silently. | Pretending the two formats behave alike |
