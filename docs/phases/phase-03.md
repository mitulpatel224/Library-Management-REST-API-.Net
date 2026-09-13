# Phase 3 — Reader / Member APIs

**Goal:** register and manage the people who borrow, with a membership lifecycle
that a librarian can defend to the member standing in front of them.

> **Status:** endpoints complete and exercised against a running server.
> **Automated tests for members are not written** — see *Still to do*.

---

## What we built

- `Member` entity with a status lifecycle, `MembershipType` with borrowing limits
- `Email` and `PhoneNumber` value objects — validating *and* normalising
- Server-issued membership numbers (`MEM-2026-00042`)
- `MemberConfiguration` / `MembershipTypeConfiguration`, `InitialSchema` migration
- 12 endpoints: search, get by id, get by printed number, register, update, and
  four status transitions (suspend / reactivate / expire / cancel), plus
  membership-type list, get-by-id and create
- `RequestProblemDetails` — one error shape for the whole API, including the
  failures that never reach a controller

---

## Concepts

### 1. Two identifiers, and why the surrogate key is not enough

`Member` carries both an `Id` (int, database-assigned) and a `MembershipNumber`
(`MEM-2026-00042`). That looks redundant until you watch the actual workflow: the
librarian has a card in front of them with a number printed on it, and no way to
know the surrogate key. `GET /api/members/number/{n}` is the lookup the job
actually performs; `GET /api/members/{id}` is the one the software performs.

The number is **issued by the server, never accepted from the caller**, which
forces an awkward shape in `RegisterAsync`: the number derives from the surrogate
key, and the database does not assign that until the insert completes. So the row
is written, the number computed, and the row updated — two saves.

Both run on the same `DbContext` and therefore in the same transaction, so a
failure on the second rolls back the first. There is no window in which a member
exists without a number.

**Rejected:** a client-supplied number (lets a caller claim an identifier that is
supposed to be issued, and collide with a future one), and a sequence or GUID
segment (loses the year, which is the part a human reads).

**Cost:** two round trips on registration, and a number whose year segment is
fixed forever at the moment of issue — which is exactly why the join date needs
bounds at *both* ends. See *Things that bit us*.

---

### 2. Value objects that normalise, not just validate

`Email` lower-cases on construction. This is the whole reason
`IX_Members_Email UNIQUE` means what it appears to mean: stored as typed, the
index happily accepts `Mitul@Example.com` *and* `mitul@example.com`, and the
library ends up with two records for one person that then diverge.

`PhoneNumber` does the same in the other direction — `+91 98250 12345` is stored
as `+919825012345`, so formatting differences do not become data differences.

The validation is deliberately loose. `Email` checks structure, not correctness:
one `@`, something before it, a dot-bearing domain after, no whitespace. It does
not attempt RFC 5322, and that is a decision:

- the full grammar permits quoted strings and bang paths no real mailbox uses, so
  implementing it accepts *more* garbage, not less;
- the canonical RFC 5322 regex is ~6,400 characters and no reviewer can verify it;
- a syntactically perfect address still may not exist. The only proof is sending
  mail to it.

**Cost:** `a@localhost` is rejected although it is a valid intranet address.
Acceptable for a public library; wrong for an internal tool.

---

### 3. Status transitions as endpoints, not a settable field

`Member.Status` has no public setter. Each transition is a named method with its
own rule, and each gets its own endpoint:

| Transition | Rule |
|---|---|
| `Suspend(reason)` | Reason required. Refuses a cancelled membership. |
| `Reactivate()` | No-op if already active. Refuses a cancelled membership. |
| `Expire()` | Refuses a cancelled membership. |
| `Cancel(reason)` | Refuses an **already-cancelled** membership. |

The alternative — a `status` field on `UpdateMemberRequest` — means a routine
correction to a phone number can silently restore borrowing rights a librarian
withdrew. `UnmappedMemberHandling.Disallow` makes that explicit: posting
`"status": 1` to the update endpoint returns 400 naming the property, rather than
ignoring it and returning 200.

**Cost:** four endpoints where one PATCH would do, and a client that wants "set
this member to suspended" must know which verb to call.

---

### 4. Idempotent where there is no payload; strict where there is

`Reactivate()` on an already-active member returns 200 and changes nothing.
`Cancel()` on an already-cancelled member returns **409**. That asymmetry is
deliberate and worth being able to defend:

Reactivate takes no payload, so returning early discards no caller intent.
Cancel carries a `reason`. A second call carries a *new* reason that cannot be
honoured — the closure reason is written once, because it may document a data
protection request. Answering 200 while discarding it tells the caller their text
was recorded when the original still stands.

That is the same trap `UnmappedMemberHandling.Disallow` exists to close, stated
in the Decisions Log as: *the DTO was right to ignore it; the 200 was wrong.*

**Rejected:** idempotent no-op for symmetry with `Reactivate` — it silently loses
the caller's input. **Cost:** a client retrying after a dropped response sees a
409 for an operation that actually succeeded. Genuine retry safety needs an
`Idempotency-Key` header, not a loosened guard; noted for Phase 5.

---

### 5. `Suspended` is revisable, `Cancelled` is not

Suspending an already-suspended member **does** overwrite the reason, and that is
not an inconsistency with the rule above. A suspension is reversible and ongoing,
so its grounds can legitimately be revised as fines accumulate. A cancellation is
terminal, and its reason is a historical record.

Different lifetimes, different rules. Recorded explicitly so it does not read as
an oversight later.

---

### 6. One error shape, including the errors no controller sees

`GlobalExceptionHandler` only sees **thrown** exceptions. A request that dies in
model binding — malformed JSON, a string where an int belongs, an unknown
property, a bad enum in the query string — never reaches an action, so nothing
throws. 415 and 405 are produced by the framework before any of our code runs.

All of those fell through to ASP.NET Core's default `ProblemDetails`, which has
**no `errorCode` at all**. Since the documented contract is that clients branch on
the code and humans read the message, an entire family of responses a client must
handle was unbranchable — and invisible until somebody wrote the client.

`RequestProblemDetails` closes it in two places:

- `InvalidModelStateResponseFactory` — builds our shape for binding failures, and
  picks a specific code (`request.malformed_json`, `request.unknown_property`,
  `request.type_mismatch`, `request.body_required`, `request.invalid`);
- `CustomizeProblemDetails` — fills in the framework-generated responses (415,
  405, unmatched-route 404).

`Customize` only writes fields that are **absent**, so a domain code like
`member.not_found` is never overwritten by a generic one.

**Cost:** one more place that must stay in step with `GlobalExceptionHandler`.

---

### 7. Binding messages are rewritten, not passed through

System.Text.Json names .NET types in its errors:

```
The JSON property 'membershipNumber' could not be mapped to any .NET member
contained in type 'Library.Application.Members.Requests.CreateMemberRequest'.
```

That hands a caller the assembly layout, namespace structure and DTO names for
free. `GlobalExceptionHandler` already refuses to leak exception text for exactly
this reason — these messages were bypassing the rule. They now become:

```json
{ "membershipNumber": ["Unknown property 'membershipNumber' is not accepted here."] }
```

with a regex backstop replacing anything still naming a `System.` / `Microsoft.` /
`Library.` type. Query and route messages (`"The value 'NotAStatus' is not valid
for Status."`) pass through untouched — they already name only the value and the
field.

**Rejected:** sanitising only in Production. That leaves a dev/prod behaviour
split which hides the problem until deploy.

---

### 8. Enums on the wire as names

`"status": 0` is unreadable without the enum definition beside it, and a client
branching on the ordinal breaks silently the day a value is inserted in the
middle. `MemberStatus` already numbers its members explicitly to survive that —
which protects the *database*. `JsonStringEnumConverter` protects the *API*.

Reads stay permissive: the converter accepts the name and the number, so a client
sending `0` today keeps working.

---

### 9. Clamp what has a sensible default; refuse what does not

The member listing treats its query string inconsistently, on purpose:

| Input | Behaviour | Why |
|---|---|---|
| `pageSize=1000000` | clamped to 100 | A caller asking for too much still gets a useful answer |
| `page=-5` | clamped to 1 | Same |
| `sortBy=DROP TABLE` | falls back to `name` | Same — and the whitelist means it was never SQL |
| `joinedFrom` > `joinedTo` | **422** | There is nothing to clamp *to* |

An inverted range cannot match anything, so the empty page it used to return was
indistinguishable from "no members joined in that window" — the caller reads a
real answer to a question they did not mean to ask.

---

### 10. Case-insensitivity has to reach the database, not just the C#

`MembershipTypeNameExistsAsync` compared `t.Name == name.Trim()`. The unique index
behind that check folds case on **SQL Server** (default CI collation) and does not
on **SQLite** (BINARY). So the same request produced a 409 on one provider and a
duplicate row on the other — which then fails index creation if that data is ever
migrated to SQL Server.

The fix compares `UPPER()` on both sides, which translates on both providers.

**This is the class of bug that makes "the SQL Server swap is a config change"
false**, and it was invisible until someone actually sent `reference only` after
`Reference Only`.

**Residual gaps, stated rather than hidden:** SQLite's `UPPER()` folds ASCII only,
and an application-level check cannot stop a race between two concurrent inserts.
Closing both needs `COLLATE NOCASE` on the column, which makes the migration model
provider-specific.

---

## Things that bit us

### The validator that disagreed with its own error message

```csharp
RuleFor(r => r.JoinedOn)
    .LessThanOrEqualTo(_ => clock.Today.AddDays(1))   // accepts TOMORROW
    .WithMessage("Join date cannot be in the future.");
```

`joinedOn: "2026-09-14"` returned **201** on 2026-09-13. The rule and its
explanation disagreed, and the message was the one stating the intent. Upper bound
is now `clock.Today`.

Worse, there was no lower bound at all — so `joinedOn: "1800-01-01"` issued
`MEM-1800-00014`. The comment above the rule anticipated only future years. The
membership number is immutable, so a mis-keyed century is permanent.

The floor now lives in `Member.Create` **as well as** the validator, because the
seeder and any future import path never pass through a validator. The upper bound
stays validator-only: the entity has no clock, and giving it one would be the
`DateTime.Now` this codebase forbids.

### CA1862 demanding a fix that does not compile to SQL

Making the name comparison case-insensitive tripped the analyzer, which wanted
`string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`. That advice is correct
for in-memory code and **wrong inside an EF Core expression tree** — EF cannot
translate the `StringComparison` overloads, so taking it swaps a build error for a
runtime *"could not be translated"* exception.

Suppressed with `#pragma` scoped to the single statement, not the file and not
`.editorconfig`, so the rule keeps protecting every ordinary comparison around it.
This is the narrow case the "do not suppress analyzers" rule explicitly allows.

### A 404 code that broke its own convention

Adding `GET /api/membership-types/{id}` produced `membershiptype.not_found` —
because `NotFoundException` did `resource.ToLowerInvariant()`. It sat directly
beside `membership_type.duplicate_name`: two conventions in one API, where the
contract promises one.

Invisible until the first *multi-word* resource existed. Every single-word code
(`member`, `book`, `copy`) was unaffected, which is exactly why it survived two
phases.

### A database that appeared to have been wiped

Restarting the API from `bin/Release/net10.0/` made
`"ConnectionString": "Data Source=library.db"` resolve relative to **that**
directory. A second, empty `library.db` was created there, migrated and seeded —
and the member list came back with only the 9 seeded rows.

Nothing was lost; the real database was still at `src/Library.Api/library.db`.
Worth knowing generally: **the dev database location silently follows the working
directory**, so `dotnet run --project src/Library.Api` and running the built
executable do not share a database unless launched from the same folder.

### A 409 that quoted the wrong spelling

Once the duplicate check was case-insensitive, sending `sTaNdArD` produced
*"A membership type named 'sTaNdArD' already exists."* — the caller's casing, not
the row's. A librarian reading that goes looking for a type that is not on file
under that name. The lookup now returns the **stored** name.

---

## SOLID in this phase

| Principle | Where |
|---|---|
| **S** | `RequestProblemDetails` does binding-error shaping only; `GlobalExceptionHandler` keeps exception mapping |
| **O** | A new status transition is a new entity method plus an endpoint — no existing transition changes |
| **L** | `IMemberRepository` → `MemberRepository`; the service never knows which it has |
| **I** | `IMemberService` exposes the member use cases; membership types are separate reads on the same repository, not a generic CRUD surface |
| **D** | `Member.Create` takes a `DateOnly`, never a clock — the caller supplies time, so the entity stays testable and Domain keeps its zero references |

The clearest dependency-rule moment: `MemberSortOptions` uses plain property
access in every expression, because reaching for `EF.Property` to address a
backing field would drag EF Core into `Library.Application`. That constraint is
why `Member.MembershipNumber` is a mapped property rather than a private field.

---

## Verification

```bash
dotnet build LibraryManagement.slnx -c Release      # 0 warnings, 0 errors
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe          # 51 passed
./tests/Library.IntegrationTests/bin/Release/net10.0/Library.IntegrationTests.exe  # 5 passed
dotnet run --project src/Library.Api
```

```bash
# Registration issues the number; the client cannot supply it
curl -X POST localhost:5112/api/members -H 'Content-Type: application/json' \
  -d '{"fullName":"Asha Nair","email":"asha@example.com","membershipTypeId":2}'
# 201, "membershipNumber":"MEM-2026-00024", "status":"Active"

# Email uniqueness is case-insensitive because the value object normalises first
curl -X POST localhost:5112/api/members -H 'Content-Type: application/json' \
  -d '{"fullName":"Dup","email":"ASHA@EXAMPLE.COM","membershipTypeId":1}'
# 409 member.duplicate_email

# The card in the librarian's hand, in any casing
curl localhost:5112/api/members/number/mem-2026-00024        # 200

# Both ends of the join date are now bounded
curl -X POST ... -d '{"...","joinedOn":"2026-09-14"}'        # 422 "cannot be in the future"
curl -X POST ... -d '{"...","joinedOn":"1800-01-01"}'        # 422 "cannot be before 1900-01-01"

# Cancellation is written once
curl -X POST localhost:5112/api/members/24/cancel -d '{"reason":"closure"}'   # 200
curl -X POST localhost:5112/api/members/24/cancel -d '{"reason":"second"}'    # 409 member.already_cancelled
curl localhost:5112/api/members/24 | grep statusReason                        # still "closure"

# Errors that never reach a controller still carry a code
curl -X POST localhost:5112/api/members -H 'Content-Type: text/plain' -d '{}'  # 415 request.unsupported_media_type
curl -X POST localhost:5112/api/members -H 'Content-Type: application/json' -d '{'  # 400 request.malformed_json
curl -X POST localhost:5112/api/members -H 'Content-Type: application/json' \
  -d '{"fullName":"X","email":"a@b.com","membershipTypeId":1,"status":1}'      # 400 request.unknown_property

# Membership type names collide case-insensitively, and the message quotes the row
curl -X POST localhost:5112/api/membership-types -d '{"name":"sTaNdArD","maxConcurrentLoans":3,"loanPeriodDays":10}'
# 409 "A membership type named 'Standard' already exists."
```

**Watch the SQL.** `Microsoft.EntityFrameworkCore.Database.Command` is at
`Information` in development. Confirm the counts:

| Request | Queries |
|---|---|
| `GET /api/members?pageSize=20` | **2** (count + page) |
| `GET /api/membership-types` (with `memberCount`) | **1** |
| `GET /api/members/{id}` | **1** |

`memberCount` is counted in SQL. Loading each type's members to call `.Count`
would be the classic N+1 on a lookup endpoint.

---

## Questions you should be able to answer

1. Why does a member need both an `Id` and a `MembershipNumber`?
2. Why does registration save twice, and what guarantees no member exists without
   a number?
3. Why does `Email` lower-case on construction rather than at the comparison?
4. Why is `Cancel` on a cancelled member a 409 when `Reactivate` on an active one
   is a 200?
5. Why may a suspension reason be revised when a cancellation reason may not?
6. Why is `status` not settable through `PUT /api/members/{id}`?
7. Why did `GlobalExceptionHandler` never see a malformed-JSON request?
8. Why is leaking `'Library.Application.…CreateMemberRequest'` in a 400 a security
   problem and not just untidy?
9. Why did the same duplicate-name request behave differently on SQLite and SQL
   Server?
10. Why can't `Member.Create` enforce "join date not in the future" itself?
11. Why is `pageSize=1000000` clamped but an inverted date range refused?
12. Why does `MemberSortOptions` avoid `EF.Property`?

---

## Still to do

- **Unit and integration tests for members.** The 51 unit tests cover books,
  paging and entities; there are none for `Member`, `MembershipType`, the value
  objects, the validators, or the status transitions. Every claim in this document
  was verified by hand against a running server, which is not a substitute.
- Automatic expiry. `MembershipType` carries no duration, so nothing can compute
  when a membership lapses — `Expire()` is reachable only by explicit call. Adding
  a duration is a modelling decision, deferred rather than guessed.
- No audit trail on status changes: nothing records *who* suspended or cancelled a
  member, or when. Meaningful only once Phase 5 supplies an identity.
- `COLLATE NOCASE` (or equivalent) on `MembershipTypes.Name`, to close the
  concurrent-insert race the application-level check cannot.
- `BookSearchRequest` very likely has the same inverted-range gap that
  `MemberSearchRequest` just fixed — unprobed.

---

## What Phase 4 builds on this

Lending needs exactly what this phase established:

- `Member.CanBorrow` — currently `Status == Active`, and the one place Phase 4
  extends when unpaid fines start blocking loans. The `canBorrowOnly` search
  filter was written to survive that change; `status=Active` would not have.
- `MembershipType.MaxConcurrentLoans` — the limit a loan must check.
- `MembershipType.LoanPeriodDays` — the due date, and therefore every overdue and
  fine calculation, derives from this.
- `IClock`, already injected into the validators, is what makes those fine
  calculations testable without changing the machine's date.
