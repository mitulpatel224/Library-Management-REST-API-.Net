# Library Management REST API

A layered .NET 10 REST API for managing a library's book catalogue, its members,
and the lending of physical copies between them.

Built as a .NET assessment. The emphasis throughout is on **why** each decision
was made — every phase ships working code alongside a concept guide in
[`docs/phases/`](docs/phases/) explaining the trade-offs it involved.

---

## Contents

- [Problem statement](#problem-statement)
- [Quick start](#quick-start)
- [Architecture](#architecture)
- [Data model](#data-model)
- [API surface](#api-surface)
- [Error handling](#error-handling)
- [Key design decisions](#key-design-decisions)
- [Project structure](#project-structure)
- [Testing](#testing)
- [Documentation index](#documentation-index)
- [Roadmap](#roadmap)

---

## Problem statement

Libraries need to track books, members, and which member currently holds which
book. The system must:

- catalogue titles and the **physical copies** the library owns of each;
- prevent issuing a copy that is **already on loan**;
- compute **overdue status** and **fines** from the due date;
- support catalogue **search, filter and sort** by ISBN, title, author,
  category, genre and publisher;
- import books from file, and export reports.

The distinction that drives the whole model: a **book** is a bibliographic
record (one per ISBN), while a **copy** is a physical item on a shelf. A library
holding three copies of *Clean Code* has one `Book` row and three `BookCopy`
rows. Loans point at a copy, which is what makes "one copy is out, two are
available" expressible at all.

---

## Quick start

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.100+).
Nothing else — the development database is SQLite and is created on first run.

```bash
git clone https://github.com/mitulpatel224/Library-Management-REST-API-.Net.git
cd "Library Management - Assessment 1"

dotnet restore LibraryManagement.slnx
dotnet build   LibraryManagement.slnx -c Release
dotnet run --project src/Library.Api
```

Then open **<http://localhost:5112/swagger>**.

On first run the app applies migrations and seeds **15 books with 41 copies**,
so there is real data to query immediately. Both are controlled by
`Database:ApplyMigrationsOnStartup` in `appsettings.Development.json` and are off
by default outside development.

Try these:

```bash
curl "http://localhost:5112/api/books?search=design"
curl "http://localhost:5112/api/books?author=Fowler&sortBy=published&sortDir=desc"
curl "http://localhost:5112/api/books/isbn/978-0-13-235088-4"
curl "http://localhost:5112/api/books/1"
```

Health probes: `/health/live` (process only) and `/health/ready` (includes the
database).

Full instructions, including the SQL Server switch, are in
[`docs/setup.md`](docs/setup.md) and [`docs/database.md`](docs/database.md).

---

## Architecture

Clean Architecture in four projects. **Dependencies point inward only**, and
that rule is enforced by project references rather than by convention — a
violation fails the build.

```
┌─────────────────────────────────────────────────────────┐
│  Library.Api            controllers, middleware, DI      │
│  ──────────────────────────────────────────────────────  │
│  Library.Infrastructure EF Core, repositories, files     │
│  ──────────────────────────────────────────────────────  │
│  Library.Application    use cases, DTOs, interfaces      │
│  ──────────────────────────────────────────────────────  │
│  Library.Domain         entities, value objects, rules   │
└─────────────────────────────────────────────────────────┘
        dependencies point DOWN this diagram only
```

| Project | Responsibility | References |
|---|---|---|
| **Library.Domain** | Entities, value objects, domain events, domain exceptions. The business rules. | **Nothing.** No EF Core, no ASP.NET Core, no NuGet packages. |
| **Library.Application** | Use cases, DTOs, validators, and the *interfaces* Infrastructure implements (`IBookRepository`, `IClock`). | Domain |
| **Library.Infrastructure** | EF Core `DbContext`, repositories, file I/O, clock, seeding. The only project that knows a database exists. | Application, Domain |
| **Library.Api** | Controllers, middleware, the composition root. | Application, Infrastructure |

**Dependency inversion, made physical.** `Library.Application` declares
`IBookRepository` and never learns that EF Core exists. `Library.Infrastructure`
implements it. At startup `Library.Api` binds one to the other. The arrow points
*inward* at compile time and *outward* at runtime — which is the whole idea, and
here the compiler checks it.

See [`docs/architecture.md`](docs/architecture.md) for the full treatment,
including where each SOLID principle shows up.

---

## Data model

Normalised to third normal form. Eight tables in the catalogue so far, with 14 indexes:

```
Category ──┐                    ┌── Publisher
(self-ref) │                    │
           ▼                    ▼
         ┌──────────────────────────┐
         │          Book            │  one row per ISBN
         │  Isbn (UQ), Title, ...   │
         └──────────────────────────┘
           │          │           │
   ┌───────┘          │           └────────┐
   ▼                  ▼                    ▼
BookAuthor       BookGenre            BookCopy      physical items
(M:N + order)    (M:N)                Barcode (UQ)
   │                  │                    │
   ▼                  ▼                    ▼
 Author            Genre              (Loan → Phase 4)
```

Points worth noting:

- **`BookAuthor` carries `AuthorOrder`.** Authorship order is meaningful —
  "Gamma, Helm, Johnson, Vlissides" is not the same credit as any permutation —
  and a plain join table has no inherent ordering.
- **`Category` is self-referencing**, so *Fiction* can contain *Science Fiction*.
- **`BookCopy.Barcode` is unique library-wide**, not merely per title: a barcode
  scanned at the desk must identify one physical item with no further context.
- **Availability** is a count over copies, never a boolean on the title.

The [1NF → 2NF → 3NF walkthrough](docs/data-model.md), showing which anomaly each
step removes, is in `docs/data-model.md`.

### The invariant that enforces the core rule

Phase 4 adds the rule the problem statement is built around, and it is enforced
in the **database**, not only in C#:

```sql
CREATE UNIQUE INDEX IX_Loan_ActiveCopy
  ON Loan(BookCopyId) WHERE ReturnedAt IS NULL;
```

A partial index in SQLite, a filtered index in SQL Server. The service checks for
an active loan first so the normal path returns a clear message — but two
concurrent issue requests can both pass that check. Only one can win the index.
**The check is for the error message; the index is the guarantee.**

---

## API surface

Implemented today (Phase 2, read side):

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/books` | Search, filter, sort and page the catalogue |
| `GET` | `/api/books/{id}` | One book with its copies |
| `GET` | `/api/books/isbn/{isbn}` | Lookup by ISBN, hyphenated or not |
| `GET` | `/api/books/{id}/copies` | Physical copies of a book |
| `GET` | `/health/live` | Liveness — does not touch the database |
| `GET` | `/health/ready` | Readiness — includes the database |

### Search parameters

```
GET /api/books
    ?search=          free text across title, subtitle, ISBN
    &isbn=            exact ISBN (hyphens ignored)
    &author=          partial match on author name
    &categoryId=      &genreId=      &publisherId=
    &publishedFrom=   &publishedTo=  &language=
    &availableOnly=   true  -> only titles with a copy on the shelf
    &sortBy=          title | isbn | published | category | created
    &sortDir=         asc | desc
    &page=1           &pageSize=20   (max 100)
```

Filters combine with AND. Response:

```json
{
  "items": [ { "id": 1, "isbn": "9780201633610", "title": "Design Patterns",
               "authors": ["Erich Gamma", "Richard Helm",
                           "Ralph Johnson", "John Vlissides"],
               "genres": ["Programming", "Software Design"],
               "totalCopies": 3, "availableCopies": 3, "isAvailable": true } ],
  "page": 1, "pageSize": 20, "totalCount": 15, "totalPages": 1,
  "hasPreviousPage": false, "hasNextPage": false
}
```

The full contract is in [`docs/api-contract.md`](docs/api-contract.md); the live
OpenAPI document is at `/openapi/v1.json`.

---

## Error handling

Every error is [RFC 9457 Problem Details](https://www.rfc-editor.org/rfc/rfc9457),
produced by one `IExceptionHandler` so no controller contains a `try`/`catch`:

```json
{
  "type": "https://httpstatuses.io/404",
  "title": "Resource not found",
  "status": 404,
  "detail": "Book with identifier '9999' was not found.",
  "instance": "GET /api/books/9999",
  "errorCode": "book.not_found",
  "traceId": "00-67ac10b3d35472c6c40d73b2f79b0dc3-ba4c84e5e22f598d-00"
}
```

`errorCode` is stable and machine-readable — clients branch on it, humans read
`detail`. `traceId` appears on every log line for the request, which is what
makes a user's screenshot of an error actionable.

| Exception | Status | Meaning |
|---|---|---|
| `NotFoundException` | `404` | The resource does not exist |
| `ConflictException` | `409` | Collides with current state (copy already on loan) |
| `BusinessRuleViolationException` | `422` | Well formed, but the rules say no |
| `ValidationException` | `422` | With a per-field `errors` object |
| anything else | `500` | Generic message only — details go to the log, never the response |

That last row is a security control: a leaked stack trace hands an attacker your
framework versions, file paths, and often your schema.

---

## Key design decisions

Each of these is argued in full in the phase guides; the short version:

**The ISBN is a value object, stored as a plain column.**
`Isbn` validates the ISBN-13 check digit and normalises hyphens away, so an
invalid ISBN cannot exist in the domain — it caught a genuine typo in this
project's own seed data. It is persisted through a private `string` backing
field rather than an EF value converter, because a converter is applied to
*both* sides of a comparison and breaks `LIKE`: EF tries to convert the pattern
`"%design%"` into an `Isbn`. The backing field keeps an ordinary indexable text
column while the domain still hands out a validated type.

**Sorting resolves through a whitelist, never string interpolation.**
`BookSortOptions.SortMap` maps allowed keys to `Expression<Func<Book, object?>>`
trees. A SQL parameter can only stand in for a *value*, never a column name — so
`ORDER BY @p` is not valid SQL and no escaping makes interpolation safe. The
caller's string is *looked up*; an unknown key falls back to the default.

**Page size is capped server-side at 100.**
Without a ceiling, `?pageSize=1000000` is a one-request denial of service.

**Filters compose onto `IQueryable`; the DTO projection happens in SQL.**
A filter that was not supplied contributes no SQL at all — no
`(@p IS NULL OR col = @p)` padding that defeats index use. Projecting *before*
materialising means author names arrive with the page rather than via one extra
query per book (no N+1), and columns nobody asked for are never read.

**`IClock` is injected; nothing calls `DateTimeOffset.UtcNow` directly.**
Overdue status and fines are pure functions of "what time is it now" versus the
due date. With the clock injected, testing a 12-day-overdue loan is three lines
instead of a twelve-day wait.

**Delete behaviours are chosen per relationship, not globally.**
`Restrict` on `Book → Category`, because deleting a category must not silently
delete every book filed under it. `Cascade` on `Book → BookCopy`, because a copy
has no meaning without its title. `SetNull` on `Book → Publisher`.

---

## Project structure

```
LibraryManagement.slnx
├── Directory.Build.props          net10.0, nullable, warnings-as-errors
├── Directory.Packages.props       central package versions
├── CLAUDE.md                      instructions for Claude Code
├── TASKS.md                       task tracker + decisions log
├── docs/                          architecture, data model, phase guides
├── src/
│   ├── Library.Domain/            Common, Entities, Enums, Exceptions, ValueObjects
│   ├── Library.Application/       Books (DTOs, service, interfaces), Common
│   ├── Library.Infrastructure/    Persistence (context, configs, migrations), Repositories
│   └── Library.Api/               Controllers, Middleware, OpenApi
└── tests/
    ├── Library.UnitTests/         domain + application, no I/O
    └── Library.IntegrationTests/  real host over a per-test SQLite database
```

---

## Testing

31 tests: 26 unit and 5 integration.

**Unit tests** cover entity identity semantics, paging clamps, and page
arithmetic — no database, no HTTP.

**Integration tests** boot the real `Program.cs` through
`WebApplicationFactory` and issue genuine HTTP requests against a private
SQLite database per test class. They verify what unit tests structurally cannot:
routing, middleware order, EF mappings, and the SQL actually generated.

SQLite rather than EF Core's InMemory provider, deliberately: InMemory is not a
relational database and ignores unique indexes and foreign keys. A suite built on
it would happily allow two active loans on one copy — the exact rule this system
exists to enforce.

```bash
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe
./tests/Library.IntegrationTests/bin/Release/net10.0/Library.IntegrationTests.exe
```

> **Known issue:** `dotnet test` does not currently work. The .NET 10 SDK retired
> the VSTest bridge, and xUnit v3 targets Microsoft.Testing.Platform instead;
> the runner configuration is still being sorted out. The test executables run
> directly and all 31 pass. Tracked as issue 1 in [`TASKS.md`](TASKS.md).

---

## Documentation index

| Document | Contents |
|---|---|
| [`docs/architecture.md`](docs/architecture.md) | Layer responsibilities, dependency rule, patterns, SOLID evidence |
| [`docs/data-model.md`](docs/data-model.md) | ERD, column dictionary, 1NF→3NF walkthrough, index strategy |
| [`docs/data-flow.md`](docs/data-flow.md) | Request pipeline and sequence diagrams |
| [`docs/setup.md`](docs/setup.md) | Prerequisites, run, troubleshooting |
| [`docs/database.md`](docs/database.md) | Connection strings, provider switching, migrations, seed data |
| [`docs/api-contract.md`](docs/api-contract.md) | Endpoints, status-code policy, conventions |
| [`docs/phases/`](docs/phases/) | Phase-by-phase concept guides |
| [`TASKS.md`](TASKS.md) | Progress tracker, open issues, decisions log |

---

## Roadmap

| Phase | Scope | State |
|---|---|---|
| 1 | Boilerplate, git, tooling | ✅ Done |
| 2 | Book APIs | 🔨 Read side done; writes in progress |
| 3 | Reader / Member APIs | ⬜ |
| 4 | Lending APIs — loans, overdue, fines | ⬜ |
| 5 | Authentication & authorization | ⬜ |
| 6 | Import books from CSV/JSON | ⬜ |
| 7 | Reports & CSV export | ⬜ |
| 8 | Security & vulnerability hardening | ⬜ |
| 9 | Reservations | ⬜ Stretch |
| 10 | CQRS refactor | ⬜ Stretch |

Fines are fixed at **₹50 per overdue day**, supplied through a delegate bound to
configuration so that making the rate librarian-configurable later is a data
change rather than a code change.
