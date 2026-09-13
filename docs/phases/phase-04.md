# Phase 4 — Lending APIs (Loan + Fine)

**Goal:** the flow the whole system exists for — a copy leaves the building, comes
back, and is charged for if it is late — with the "one copy, one borrower" rule
that holds even when two requests arrive at once.

> **Status:** complete. 142 unit + 5 integration tests; every endpoint exercised
> against a running server.

---

## What we built

- `Loan` and `Fine` entities, with the lending invariant enforced by a **filtered
  unique index** rather than by a service check alone
- `LoanStatus` computed from dates, never stored
- `LoanReturnedEvent` and a **post-commit domain event dispatcher**
- `FineAssessmentHandler` — ₹50/day via `FineRateResolver` + `FineOptions`
- `INotificationService` using the C# `event` keyword, as the deliberate contrast
- SQLite `DateTimeOffset` value conversion (without it, `ORDER BY` on any
  timestamp throws)
- 9 endpoints: issue, return, renew, loan search, loan by id, fine search, fine by
  id, pay, waive — plus `GET /api/members/{id}/loans` and `/balance`
- Seeded lending history, including deliberately overdue loans
- Member and loan test suites: **56 → 147 tests**

---

## Concepts

### 1. The check is for the message; the index is the rule

"A copy that is out cannot be issued again" is checked three times:

1. `BookCopy.MarkOnLoan()` refuses a copy that is not `Available`
2. `LoanService` looks for an existing open loan, so the error can name it
3. `UX_Loans_BookCopyId_Active` — a **unique index over only the open loans**

Only the third is a guarantee. The first two both **read before they write**, so
two concurrent requests can pass them both and each proceed to insert. The index
is the only participant that serialises the writes.

```sql
CREATE UNIQUE INDEX "UX_Loans_BookCopyId_Active"
  ON "Loans" ("BookCopyId") WHERE "ReturnedAt" IS NULL;
```

The filter is what makes the index expressible at all. `Loan(BookCopyId)` cannot
be unique outright — a copy is lent hundreds of times over its life. It is unique
only among rows where `ReturnedAt IS NULL`, which is exactly "currently out".

The loser's `DbUpdateException` reaches `UnitOfWork`, which turns it into a
`ConflictException`, and `LoanService` re-labels it `loan.copy_already_on_loan` —
so a detected conflict and a lost race are indistinguishable to the client. That
is correct: both mean *someone else has it*.

**Verified:** two members racing the same copy produced exactly one 201 and one
409, twice over. And returning a copy then re-issuing it **succeeds** — which is
the proof that the index is *partial*. An unfiltered unique index would reject the
second loan of every copy ever made.

**Cost:** the rule now lives in the schema, so it cannot be unit-tested without a
database, and a provider whose filter syntax differs needs its own handling.

---

### 2. `HasFilter` takes raw SQL, so a "portable" feature isn't

Both providers support partial/filtered indexes. But `HasFilter` passes its string
through to the DDL **untranslated, identifier quoting included**:

| Provider | Correct filter |
|---|---|
| SQLite | `"ReturnedAt" IS NULL` |
| SQL Server | `[ReturnedAt] IS NULL` |

Hard-coding either puts one provider's syntax into the other's migration. So the
filter is applied in `LibraryDbContext.OnModelCreating` from `Database.ProviderName`,
and an unknown provider **throws at startup** rather than silently producing an
index with no filter — which would be unique across *all* loans for a copy and
reject the second time any book was ever lent.

This is the same class of problem as the `MembershipTypes.Name` collation bug in
Phase 3, and the second time in two phases that "the provider swap is a config
change" needed active work to stay true.

---

### 3. Domain events vs the C# `event` keyword

Both mechanisms are used, on purpose, and the contrast is the point.

| | C# `event` | Domain event |
|---|---|---|
| **Runs** | immediately, inline, on the raising thread | after the transaction commits |
| **On rollback** | already fired — cannot be recalled | never dispatched |
| **Wiring** | subscribers attach to an instance at runtime | handlers resolved from DI by type |
| **Failure** | propagates into the raiser | logged and isolated |
| **Used here for** | notifications | assessing a fine |

A fine is a financial record, so it must not exist for a return that was rolled
back. `Loan.Return()` therefore only **collects** `LoanReturnedEvent`; nothing is
invoked until `SaveChangesAsync` has succeeded.

A notification is advisory — it writes nothing, and nobody is harmed if a
subscriber misses it — so firing it inline is fine.

**The cost of post-commit dispatch, stated plainly:** the fine is written in a
*separate transaction*. There is a window where the copy is back and no fine
exists, and if the handler throws, that window never closes on its own. That is
the deliberate trade: **a late fine beats a lost return.** Handler failures are
logged with the event type so they can be replayed.

---

### 4. `LoanStatus` is computed, never stored

A loan becomes overdue at midnight on its due date **with nothing writing to its
row**. A stored status column would need a nightly job to stay honest, and would
be wrong between the due date and whenever that job next ran.

So the enum is derived from `ReturnedAt` and `DueAt` against the current time.
Filtering still happens in SQL — the three states are expressible as date
predicates — because materialising every loan to discard the ones that are not
overdue would defeat paging entirely.

```csharp
LoanStatus.Overdue => query.Where(l => l.ReturnedAt == null && l.DueAt < now),
```

---

### 5. `IClock`, finally earning its keep

Every overdue and fine assertion in `LoanTests` would otherwise need a 16-day wait
or a back-dated row that quietly assumes the arithmetic is symmetric — and it is
that assumption, not the wait, that hides bugs.

`Loan` holds no clock. Every method that needs the time takes it as a parameter,
because `Library.Domain` references nothing and reading `DateTimeOffset.UtcNow`
there would make all of this untestable.

Two rounding decisions, deliberately opposite:

- **Days overdue truncates.** A copy an hour late is not yet a day late. Charging
  ₹50 for being an hour late is not a rule a librarian wants to defend.
- **Days remaining rounds up.** A loan issued moments ago for 14 days has 13.999
  days left; truncating told the member they had 13. The desk promised 14.

---

### 6. Open/Closed on the one rule known to be about to change

The fine rate is ₹50/day today and everyone expects that to change.
`FineOptions` binds it from configuration; `FineRateResolver` — a one-method
delegate — is what consumers actually depend on.

Depending on the delegate rather than `IOptions<FineOptions>` means the handler
cannot read `Currency`, cannot break when a field is added, and is tested with
`() => 50m` — no options plumbing, no mock. `FineAssessmentHandlerTests`
demonstrates exactly that.

It stops short of a database table, deliberately: a librarian-editable rate needs
an audit trail, an effective-from date, and a rule for loans spanning a change.
Configuration is honest about the flexibility actually provided.

The rate is **captured onto the Fine row** at assessment. `Fine.RatePerDay` is a
record of what was charged, not a live lookup — changing the schedule next year
must not silently restate last year's fines.

---

### 7. Where each rule lives

| Rule | Validator | Entity | Database |
|---|---|---|---|
| Ids present | per-field message | `> 0` guard | FK |
| Member may borrow | — | `Loan.Issue` | — |
| Copy is available | — | `BookCopy.MarkOnLoan` | — |
| **One open loan per copy** | — | best-effort | **`UX_Loans_BookCopyId_Active`** |
| Loan limit | — | — (service) | — |
| One fine per loan | — | handler pre-check | `UX_Fines_LoanId` |
| Waiver reason required | per-field message | `Fine.Waive` | — |

The validator produces the message a form can display. The entity guarantees the
rule for callers that never see a validator — the seeder, any future import. The
database is the only layer that can enforce anything under concurrency.

---

## Things that bit us

### SQLite cannot `ORDER BY` a `DateTimeOffset`

`GET /api/loans` returned **500**:

```
System.NotSupportedException
  at SqliteQueryableMethodTranslatingExpressionVisitor.TranslateOrderBy
```

SQLite has no date type. The provider stores a `DateTimeOffset` as TEXT with its
offset appended — `2026-09-13 16:54:21.647+00:00` — and text sorts
lexicographically, so two instants an hour apart in different offsets would sort
in the wrong order. EF Core refuses to translate the ordering rather than return a
wrong answer quietly. The loan listing sorts by due date, so it met that refusal
immediately.

Fixed with a SQLite-only value converter to UTC `DateTime`. Nothing is lost:
every timestamp here comes from `IClock.UtcNow`, so the offset is always zero. What
is gained is a stored form whose lexicographic order **is** chronological order.
SQL Server is untouched — `datetimeoffset` is a real type there and sorts
correctly.

This one is worth remembering: **a passing `WHERE` clause does not mean `ORDER BY`
will work on the same column.**

### A null-conditional that silently stranded copies

Two unit tests failed, and they had found a real bug rather than a test artifact:

```csharp
BookCopy?.MarkReturned(condition);   // wrong
```

With the navigation unloaded, the loan closed while the copy stayed `OnLoan` —
and because the filtered index only constrains *open* loans, nothing would ever
point at the problem. The copy would simply never be lendable again.

Fixed twice over: `Loan.Issue` now sets the navigation as well as the id, and
`Return` **throws** instead of skipping. A copy silently stranded off the shelf is
far worse than a loud failure.

`?.` is a null check that looks like a safety feature. Here it was suppressing the
exact signal that mattered.

### A 500 where a 409 belonged

Renewing a closed loan returned 500. `RenewAsync` defaults the renewal period to
`loan.Member.MembershipType.LoanPeriodDays`, but `GetEntityAsync` included
`Member` without `ThenInclude(m => m.MembershipType)` — so the null dereference
happened *before* the entity could refuse the renewal.

The lesson is about ordering: a guard that runs after the code it protects is not
a guard.

### Overdue behaviour cannot be reached by calling the API

A fresh loan is not late for a fortnight, so the overdue report and the entire
fine path were unverifiable by hand. `SeedLoansAsync` now creates back-dated
loans — 16 and 61 days overdue — relative to `IClock`, so they stay overdue
whenever the database is rebuilt. A hard-coded date would be correct on the day it
was written and wrong every day after.

---

## SOLID in this phase

| Principle | Where |
|---|---|
| **S** | `FineAssessmentHandler` assesses fines and nothing else; `ReturnAsync` takes a copy back and does not know fines exist |
| **O** | The fine rate moves behind `FineRateResolver`; a new reaction to a return is a new handler, not an edit to `ReturnAsync` |
| **L** | `IDomainEventHandler<T>` — the dispatcher invokes any handler without knowing which |
| **I** | `ILoanRepository` and `IFineRepository` are separate; the fine handler cannot reach loan writes |
| **D** | `FineAssessmentHandler` depends on a delegate it can be handed, not on the configuration system |

The clearest moment: `Loan` takes `DateTimeOffset issuedAt` as a parameter. The
domain cannot reach a clock, so the caller supplies time — which is exactly what
makes every overdue test in this phase possible.

---

## Verification

```bash
dotnet build LibraryManagement.slnx -c Release      # 0 warnings, 0 errors
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe          # 142 passed
./tests/Library.IntegrationTests/bin/Release/net10.0/Library.IntegrationTests.exe  # 5 passed
dotnet run --project src/Library.Api
```

The seeded database starts with two overdue loans, so the interesting paths are
reachable immediately:

```bash
# The chase list
curl "localhost:5112/api/loans?overdueOnly=true"
# loan 2: 16 days overdue · loan 3: 61 days overdue

# Returning a late copy assesses the fine AFTER the return commits
curl -X POST localhost:5112/api/loans/2/return -H 'Content-Type: application/json' -d '{}'
# 200, fine: { daysOverdue: 16, ratePerDay: 50, amount: 800 }

curl localhost:5112/api/members/2/balance
# totalOutstanding: 800, unsettledFineCount: 1

# The copy went back on the shelf and can be lent again - this is the proof
# that the unique index is PARTIAL and not simply unique on BookCopyId
curl -X POST localhost:5112/api/loans/issue -H 'Content-Type: application/json' \
  -d '{"bookCopyId":40,"memberId":1}'          # 201

# Issuing a copy that is out
curl -X POST localhost:5112/api/loans/issue -d '{"bookCopyId":40,"memberId":1}'
# 409 loan.copy_already_on_loan, naming the holder and the due date

# An overdue loan cannot be renewed - that would erase the accrued fine
curl -X POST localhost:5112/api/loans/3/renew -d '{}'
# 409 loan.overdue_cannot_renew

# Settlement
curl -X POST localhost:5112/api/fines/1/pay -d '{}'        # 200
curl -X POST localhost:5112/api/fines/1/pay -d '{}'        # 409 fine.already_paid
curl -X POST localhost:5112/api/fines/1/waive -d '{"reason":""}'   # 422
```

**Query counts** — `Microsoft.EntityFrameworkCore.Database.Command` is at
`Information` in development:

| Request | Queries |
|---|---|
| `GET /api/loans?pageSize=20` | **2** (count + page) |
| `GET /api/fines?pageSize=20` | **2** |
| `GET /api/members/{id}/loans` | **3** (exists + count + page) |
| `GET /api/members/{id}/balance` | **4** independent aggregates, not per-row |

---

## Questions you should be able to answer

1. Why is the service's "is this copy out?" check not enough?
2. Why must the unique index be *filtered*? What breaks without the filter?
3. Why is `ReturnedAt` nullable rather than a `bool IsReturned` beside a date?
4. Why does `HasFilter` need a provider conditional when both providers support
   the feature?
5. Why are domain events dispatched after the commit, and what is the cost?
6. When is a C# `event` the right choice, and when is it actively wrong?
7. Why is `LoanStatus` not a column?
8. Why does `Fine` store `RatePerDay` when the rate is in configuration?
9. Why do days overdue truncate while days remaining round up?
10. Why can't `Loan` read the clock itself?
11. Why did `ORDER BY DueAt` throw on SQLite when `WHERE DueAt < now` worked?
12. Why is `BookCopy?.MarkReturned()` worse than letting it throw?
13. Why is the loan limit checked in the service rather than in `Loan.Issue`?

---

## Still to do

- **Integration tests for the concurrency race.** The race is proven by hand
  through the API, and the index is proven by the re-issue path, but there is no
  automated test that fires two concurrent issues. That is the one claim in this
  document resting on manual verification.
- Unpaid fines do not block borrowing. `Member.CanBorrow` is still
  `Status == Active`; `GET /api/members/{id}/balance` reports the debt but nothing
  acts on it. Whether an outstanding fine should stop a loan is a policy decision,
  not an oversight — deliberately not invented here.
- No partial payment. `POST /api/fines/{id}/pay` settles in full.
- No audit of who issued, returned, waived or paid — Phase 5, once there is an
  identity to attribute it to.
- `Fine.MaxAmount` (₹5,000) is a domain constant, not configuration, unlike the
  rate.
- Handler failures are logged but not retried. A failed fine assessment needs
  someone to read the log.

---

## What Phase 5 builds on this

- Every write here is anonymous. Auth supplies the identity that makes
  `issuedBy` / `waivedBy` recordable — and waiving money is the operation that
  most needs it.
- `POST /api/fines/{id}/waive` is the clearest candidate for role-based
  authorization: a librarian may waive, a member may not.
- Resource-based authorization has its first real subject — a member should see
  their own loans and balance, not everyone's.
- The retry-safety gap on `return` and `cancel` (a dropped response then a retry
  yields 409) is properly solved with an `Idempotency-Key` header, which belongs
  with the auth work.
