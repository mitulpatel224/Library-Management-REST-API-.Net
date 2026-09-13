# Data Flow

How a request travels through the system, and what each layer contributes.

---

## The request pipeline

Every request passes through the same middleware stack before reaching a
controller. **Order is behaviour here, not style** — each component wraps the
ones after it, so a component only ever sees what the earlier ones have already
done.

```mermaid
flowchart TD
    REQ([HTTP request]) --> EX[UseExceptionHandler<br/>FIRST: wraps everything below]
    EX --> SCP[UseStatusCodePages<br/>bare 404 becomes ProblemDetails]
    SCP --> LOG[UseSerilogRequestLogging<br/>one line: method, path, status, duration]
    LOG --> SWG{Development?}
    SWG -->|yes| OAPI[MapOpenApi + Swagger UI]
    SWG --> HTTPS[UseHttpsRedirection]
    HTTPS --> AUTH[UseAuthentication -> UseAuthorization<br/>Phase 5]
    AUTH --> ROUTE[MapControllers]
    ROUTE --> CTRL[Controller action]
    CTRL --> SVC[Application service]
    SVC --> REPO[Repository]
    REPO --> EF[EF Core translates<br/>expression tree to SQL]
    EF --> DB[(SQLite / SQL Server)]
    DB --> RESP([JSON response])

    style EX fill:#da3633,color:#fff
    style DB fill:#1f6feb,color:#fff
    style AUTH fill:#9e6a03,color:#fff
```

Two placements worth calling out:

- **`UseExceptionHandler` is first**, because it must wrap everything downstream.
  Anything registered before it throws into the void.
- **`UseAuthentication` before `UseAuthorization`** (Phase 5) is the single most
  common ASP.NET Core pipeline mistake. Reversed, authorization evaluates an
  anonymous principal and every `[Authorize]` endpoint returns 401 no matter how
  valid the token is.

---

## Searching the catalogue

`GET /api/books?search=design&availableOnly=true&sortBy=title&page=1&pageSize=20`

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant Ctl as BooksController
    participant Svc as BookService
    participant Repo as BookRepository
    participant EF as EF Core
    participant DB as Database

    C->>Ctl: GET /api/books?search=design&...
    Note over Ctl: [ApiController] binds the query<br/>string to BookSearchRequest
    Ctl->>Svc: SearchAsync(request, ct)
    Svc->>Repo: SearchAsync(request, ct)

    Note over Repo: Normalize() clamps page/pageSize.<br/>pageSize is capped at 100.

    Repo->>Repo: AsNoTracking()
    Repo->>Repo: ApplyFilters - appends only the<br/>predicates actually supplied
    Note over Repo,EF: Nothing has executed yet.<br/>IQueryable is still an expression tree.

    Repo->>EF: CountAsync()
    EF->>DB: SELECT COUNT(*) ... WHERE ...
    DB-->>EF: 5
    EF-->>Repo: totalCount

    alt totalCount == 0
        Repo-->>Svc: PagedResult.Empty
    else
        Repo->>Repo: ApplySorting - whitelisted expression<br/>+ ThenBy(Id) tiebreaker
        Repo->>EF: Skip/Take + Select(BookSummaryDto)
        EF->>DB: SELECT columns... LEFT JOIN authors,<br/>genres, copies ... ORDER BY ... LIMIT
        DB-->>EF: rows
        EF-->>Repo: List&lt;BookSummaryDto&gt;
    end

    Repo-->>Svc: PagedResult&lt;BookSummaryDto&gt;
    Svc-->>Ctl: (passes through unchanged)
    Ctl-->>C: 200 OK + JSON
```

### What makes this two queries and not twenty-one

The projection happens **inside** the `IQueryable`, before materialisation:

```csharp
await query
    .Skip(...).Take(...)
    .Select(book => new BookSummaryDto
    {
        Title   = book.Title,
        Authors = book.BookAuthors.OrderBy(ba => ba.AuthorOrder)
                      .Select(ba => ba.Author.FirstName + " " + ba.Author.LastName)
                      .ToList(),
        AvailableCopies = book.Copies.Count(c => c.Status == CopyStatus.Available),
    })
    .ToListAsync(ct);
```

EF Core translates that shape into the SQL column list and the joins. Two
consequences:

- **No N+1.** Author names arrive with the page, not through one extra query per
  book. Calling `ToListAsync()` first and mapping afterwards would produce
  exactly that: 1 query for the page plus 20 for the authors plus 20 for the
  genres.
- **No over-fetching.** `Description` and `PageCount` are never read, because the
  summary DTO does not mention them.

`AvailableCopies` is counted **in SQL**. The domain has a
`Book.AvailableCopies` property, but it counts a loaded collection — using it
here would mean loading all 41 copies to display a number.

### Why the count query runs separately

`CountAsync` runs against the filtered query but **without** the ordering or
paging. That is what makes `totalCount` mean "matches" rather than "matches on
this page", and it is what lets the client render "page 1 of 8".

### Why sorting always appends a tiebreaker

```csharp
return ordered.ThenBy(b => b.Id);
```

SQL guarantees no ordering among rows that tie on the `ORDER BY` key. With fifty
books published in 2024 and `sortBy=published`, the database may return them in
any order — and a *different* order on the next call. Page 2 would then repeat or
skip rows that page 1 already showed. A unique tiebreaker makes the total
ordering deterministic and paging stable.

---

## Fetching one book

`GET /api/books/9999` — the not-found path, which is where the layering earns
its keep.

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant Ctl as BooksController
    participant Svc as BookService
    participant Repo as BookRepository
    participant GEH as GlobalExceptionHandler

    C->>Ctl: GET /api/books/9999
    Ctl->>Svc: GetByIdAsync(9999, ct)
    Svc->>Repo: GetByIdAsync(9999, ct)
    Repo-->>Svc: null
    Note over Svc: The repository reports what IS.<br/>The service decides what a miss MEANS.
    Svc--xCtl: throw NotFoundException("Book", 9999)
    Note over Ctl: No try/catch here - deliberately.
    Ctl--xGEH: exception propagates
    GEH->>GEH: Map(exception) -> 404, "book.not_found"
    GEH-->>C: 404 + ProblemDetails + traceId
```

The controller contains no error handling at all. One handler maps every
exception in the application, so the safe behaviour is the default and the only
behaviour — rather than something each of forty actions has to remember.

---

## Registering a member

Two saves in one transaction, because the membership number is derived from a key
the database does not assign until the insert completes.

```mermaid
sequenceDiagram
    participant C as Client
    participant F as ValidationFilter
    participant S as MemberService
    participant R as MemberRepository
    participant DB as Database

    C->>F: POST /api/members
    F->>F: CreateMemberRequestValidator<br/>(shape, lengths, joinedOn bounds)
    alt invalid
        F-->>C: 422 validation.failed + per-field errors
    end
    F->>S: RegisterAsync(request)

    S->>S: Email.Create - validates AND lower-cases
    S->>S: PhoneNumber.Create - strips formatting

    S->>R: EmailExistsAsync(normalised)
    R->>DB: SELECT 1 FROM Members WHERE Email = @e
    alt already registered
        S-->>C: 409 member.duplicate_email
    end

    S->>R: MembershipTypeExistsAsync(id)
    R->>DB: SELECT 1 FROM MembershipTypes WHERE Id = @id
    alt missing
        S-->>C: 422 member.membership_type_not_found
    end

    S->>S: Member.Create(...) - enforces name,<br/>type and the 1900 join-date floor
    S->>R: Add(member)
    S->>DB: SaveChanges  (1) INSERT - key assigned here
    S->>S: member.AssignMembershipNumber()<br/>MEM-{JoinedOn:yyyy}-{Id:00000}
    S->>DB: SaveChanges  (2) UPDATE with the number

    S->>R: GetByIdAsync(id)
    R->>DB: SELECT ... JOIN MembershipTypes
    S-->>C: 201 + Location: /api/members/{id}
```

### Why two saves rather than one

The number embeds the surrogate key, and the key does not exist until the row is
inserted. The alternatives were worse: a client-supplied number lets a caller
claim an identifier that is meant to be issued, and a separate sequence table adds
a second thing to keep in step with the first.

Both saves share one `DbContext` and therefore one ambient transaction, so a
failure on the second rolls back the first. **There is no window in which a member
exists without a number** — which matters because the number is the only
identifier the librarian can see.

### Why normalisation happens before the uniqueness check

`Email.Create` lower-cases, and the check compares the normalised value. Reversed,
`ASHA@EXAMPLE.COM` would pass a check against the stored `asha@example.com` and
then fail at the unique index — a 500 where a 409 was correct.

### Where each rule lives, and why it is not duplication

| Rule | Validator | Entity | Database |
|---|---|---|---|
| Name present, ≤ 200 chars | per-field message | invariant | `NOT NULL`, length |
| Email well-formed | per-field message | `Email.Create` throws | length |
| Email unique | — | — | `IX_Members_Email UNIQUE` |
| `joinedOn` ≥ 1900-01-01 | per-field message | `Member.Create` throws | — |
| `joinedOn` ≤ today | per-field message | — (no clock in Domain) | — |

The validator produces the message a form can display beside the offending input.
The entity guarantees the rule holds for callers that never pass through a
validator — the seeder, and any future bulk import. The database is the last line,
and the only one that can enforce uniqueness under concurrency.

The one deliberate hole: "not in the future" cannot live in the entity, because
the entity has no clock and reading one would be the `DateTime.Now` this codebase
forbids everywhere. So a bulk import could, today, set a future join date.

---

## Changing a member's status

```mermaid
stateDiagram-v2
    [*] --> Active: register
    Active --> Suspended: suspend (reason required)
    Suspended --> Active: reactivate
    Active --> Expired: expire
    Expired --> Active: reactivate
    Suspended --> Expired: expire
    Active --> Cancelled: cancel
    Suspended --> Cancelled: cancel
    Expired --> Cancelled: cancel
    Cancelled --> [*]: terminal
```

`Cancelled` has no outbound edge. Suspend, reactivate and expire all return
**409 `member.cancelled`** against it, and a second cancel returns **409
`member.already_cancelled`** rather than overwriting the closure reason.

Reactivating an *already-active* member returns 200 and changes nothing. Cancelling
an already-cancelled one returns 409. The asymmetry is deliberate: reactivate
carries no payload, so returning early discards no caller intent, whereas cancel
carries a reason that cannot be honoured — and answering 200 while discarding it
would report a revision that never happened.

---

## Issuing a loan

The flow that the whole system exists for, including the concurrency path.

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant Ctl as LoansController
    participant Svc as LoanService
    participant Dom as Loan / BookCopy
    participant DB as Database
    participant Disp as DomainEventDispatcher

    C->>Ctl: POST /api/loans/issue {bookCopyId, memberId}
    Ctl->>Svc: IssueAsync(command, ct)

    Svc->>DB: load copy + member + active loan count
    alt copy already on loan
        Svc--xC: 409 copy.already_on_loan
    else member at MaxConcurrentLoans
        Svc--xC: 422 member.loan_limit_reached
    end

    Svc->>Dom: Loan.Issue(copy, member, clock.UtcNow, loanPeriodDays)
    Note over Dom: DueAt = IssuedAt + LoanPeriodDays<br/>computed from the INJECTED clock
    Dom->>Dom: copy.MarkOnLoan()

    Svc->>DB: SaveChangesAsync
    alt unique index violated by a concurrent request
        DB--xSvc: DbUpdateException
        Note over Svc: The check above lost a race.<br/>The INDEX is what actually enforces the rule.
        Svc--xC: 409 copy.already_on_loan
    else committed
        DB-->>Svc: ok
        Svc->>Disp: dispatch events raised during the transaction
        Note over Disp: AFTER commit. A rollback must not<br/>leave a fine assessed for a return<br/>that never happened.
        Disp-->>Svc: handled
        Svc-->>C: 201 Created
    end
```

### Why events dispatch after the commit

A C# `event` invokes its subscribers synchronously, inline, at the moment it is
raised — which here would be *before* the transaction commits. If the transaction
then rolled back, a fine would have been assessed for a return that never
happened.

So entities only **collect** events, and the infrastructure dispatches them once
`SaveChangesAsync` has succeeded.

The project also uses the C# `event` keyword deliberately, in the notification
service, where fire-and-forget in-process notification *is* the correct semantic.
The contrast between the two mechanisms is the point.

---

## Returning a book and assessing a fine

```mermaid
flowchart LR
    RET[POST /api/loans/id/return] --> LOAD[Load loan + copy]
    LOAD --> CLOSE[loan.Return - clock.UtcNow]
    CLOSE --> CHK{ReturnedAt > DueAt?}
    CHK -->|no| SAVE[SaveChanges]
    CHK -->|yes| EVT[Raise LoanReturnedEvent]
    EVT --> SAVE
    SAVE --> DISP[Dispatch events post-commit]
    DISP --> FINE[FineAssessmentHandler]
    FINE --> CALC["Fine = daysOverdue x FineRateResolver()"]
    CALC --> PERSIST[(Fine row)]

    style CHK fill:#9e6a03,color:#fff
    style DISP fill:#238636,color:#fff
```

`FineRateResolver` is a delegate bound to `FineOptions.RatePerDay` (₹50) from
configuration. That indirection is what makes the rate librarian-configurable
later without touching `Loan` — Open/Closed applied to a business rule that is
known to be about to change.

---

## Importing books (Phase 6 — planned)

```mermaid
flowchart TD
    UP[POST /api/books/import<br/>multipart/form-data] --> GUARD{Size, extension,<br/>content type OK?}
    GUARD -->|no| REJ[400 + reason]
    GUARD -->|yes| STREAM[Open IFormFile as a STREAM<br/>never ReadAllBytes]
    STREAM --> CSV[CsvHelper: IAsyncEnumerable of rows]
    CSV --> ROW[Per row: validate + Isbn.TryCreate]
    ROW --> OK{Valid?}
    OK -->|no| ERRS[Collect row number + reason]
    OK -->|yes| DEDUP{ISBN exists?}
    DEDUP -->|yes| STRAT[Strategy: skip / update / fail]
    DEDUP -->|no| BATCH[Buffer into a batch]
    STRAT --> BATCH
    BATCH --> TX[(Insert in one transaction)]
    ERRS --> REPORT
    TX --> REPORT[207-style report:<br/>accepted + rejected rows]

    style STREAM fill:#238636,color:#fff
    style GUARD fill:#da3633,color:#fff
```

Streaming rather than buffering is the point: a 200 MB upload read with
`ReadAllBytes` puts 200 MB on the large object heap. Streaming keeps memory
roughly constant regardless of file size.

`Isbn.TryCreate` — the non-throwing parse — exists for exactly this path, so one
bad row is reported rather than aborting the batch.

---

## Where the layers actually sit

Reading the same call from top to bottom:

| Layer | Type | Knows about | Returns |
|---|---|---|---|
| API | `BooksController` | HTTP, status codes | `ActionResult<T>` |
| Application | `BookService` | Use cases, what a miss means | DTO, or throws |
| Application | `IBookRepository` | *Nothing* — an interface only | `PagedResult<T>` |
| Infrastructure | `BookRepository` | EF Core, `IQueryable`, SQL | Materialised DTOs |
| Infrastructure | `LibraryDbContext` | Tables, change tracking | Entities |
| Database | | | Rows |

The rule that keeps this honest: **`IQueryable` never escapes the repository.**
Returning one would let a controller compose another `Where` onto a live query
and fire SQL from inside a view — and would make the Application layer depend on
EF Core semantics it is supposed to know nothing about.
