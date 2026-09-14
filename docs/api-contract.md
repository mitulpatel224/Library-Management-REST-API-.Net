# API Contract

Conventions, endpoints, and the error shape.

Live OpenAPI document: `/openapi/v1.json` · Interactive UI: `/swagger`

---

## Conventions

**Base path** `/api`. **Format** JSON only, `application/json`.
**Casing** camelCase, which is the ASP.NET Core default.

**Resource naming** is plural nouns, with verbs expressed by the HTTP method:
`/api/books`, not `/api/getBooks`. Sub-resources nest where the child has no
independent meaning — `/api/books/{id}/copies`, because a copy without its title
is nothing. Where a child *does* stand alone it gets a top-level route:
`DELETE /api/copies/{id}` addresses a specific physical item directly.

**Dates** are ISO 8601. A date with no time is `DateOnly` (`"1994-10-31"`);
an instant is `DateTimeOffset` in UTC (`"2026-09-12T13:45:00+00:00"`).
`DateTimeOffset` rather than `DateTime` because it carries an explicit offset and
so cannot be silently reinterpreted in another time zone.

---

## Status codes

**Enums travel as names, not numbers.** `"status": "Active"`, never `"status": 0`.
An ordinal is unreadable without the enum definition beside it, and silently
changes meaning if a value is ever inserted in the middle. Reads stay permissive:
the name and the number are both accepted on input.


| Code | When | Notes |
|---|---|---|
| `200 OK` | Successful read or update | An empty result set is still 200 |
| `201 Created` | Resource created | `Location` header points at it |
| `204 No Content` | Successful delete | No body |
| `400 Bad Request` | Malformed request | Model binding — a string where an int belongs |
| `401 Unauthorized` | Missing or invalid token | Phase 5. Means *unauthenticated* |
| `403 Forbidden` | Authenticated but not permitted | Phase 5 |
| `404 Not Found` | Resource does not exist | |
| `405 Method Not Allowed` | Wrong verb for the route | |
| `409 Conflict` | Collides with current state | Copy already on loan; duplicate ISBN |
| `415 Unsupported Media Type` | Missing or wrong `Content-Type` | Send `application/json` |
| `422 Unprocessable Entity` | Well formed, but a rule says no | Validation failures, business rules |
| `500 Internal Server Error` | A bug | Generic message only — details never leave the server |

### 400 vs 422 vs 409

These get conflated, so the rule this API applies:

- **400** — the request could not be *understood*. `?page=abc`. Caught by model
  binding; never reaches the domain.
- **422** — understood, but the *request itself* is the problem. "This member
  already has 5 books out." Retrying unchanged will always fail.
- **409** — understood and valid, but collides with *current state*. "That copy
  is already on loan." Retrying later, unchanged, may well succeed.

The distinguishing question: *could this succeed later without changing the
request?* Yes → 409. No → 422.

---

## Error format

Every error is [RFC 9457 Problem Details](https://www.rfc-editor.org/rfc/rfc9457),
`application/problem+json` — including framework-generated ones like an unmatched
route, so a client never parses two error formats.

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

| Field | Purpose |
|---|---|
| `errorCode` | **Stable, machine-readable.** Branch on this — never on `detail`, which is prose and may be reworded |
| `traceId` | Appears on every log line for this request. Makes a screenshot of an error actionable |
| `detail` | For humans |

Validation failures add a per-field `errors` object:

```json
{
  "status": 422,
  "title": "One or more validation errors occurred",
  "errorCode": "validation.failed",
  "errors": {
    "Isbn":  ["ISBN check digit is invalid."],
    "Title": ["Title is required.", "Title must be 500 characters or fewer."]
  }
}
```

### Error codes

| Code | Status | Meaning |
|---|---|---|
| `book.not_found` | 404 | |
| `book.title_required` | 422 | |
| `book.invalid_isbn` | 422 | Not 13 digits, or the check digit fails |
| `book.category_required` | 422 | |
| `book.invalid_page_count` | 422 | Must be > 0 |
| `book.duplicate_author` | 422 | Same author listed twice on one book |
| `copy.duplicate_barcode` | 409 | |
| `copy.barcode_required` | 422 | |
| `copy.not_available` | 409 | Not issuable in its current status |
| `copy.on_loan` | 409 | Cannot withdraw a copy that is out |
| `category.name_required` | 422 | |
| `category.cannot_parent_itself` | 422 | Direct self-parenting |
| `author.last_name_required` | 422 | |
| `genre.name_required` | 422 | |
| `publisher.name_required` | 422 | |
| `member.not_found` | 404 | |
| `member.duplicate_email` | 409 | Compared case-insensitively - the address is normalised first |
| `member.already_cancelled` | 409 | Closure is written once; the original reason stands |
| `member.cancelled` | 409 | Cancelled is terminal: cannot suspend, reactivate or expire |
| `member.membership_type_not_found` | 422 | |
| `member.name_required` | 422 | |
| `member.membership_type_required` | 422 | |
| `member.suspension_reason_required` | 422 | |
| `member.join_date_too_early` | 422 | Before 1900-01-01 |
| `member.invalid_email` | 422 | |
| `membership_type.not_found` | 404 | |
| `membership_type.duplicate_name` | 409 | Case-insensitive; the message quotes the STORED spelling |
| `loan.not_found` | 404 | |
| `loan.copy_already_on_loan` | 409 | The copy is out. Same answer whether detected or lost to a race |
| `loan.already_returned` | 409 | |
| `loan.overdue_cannot_renew` | 409 | Renewing would erase an accrued fine |
| `loan.invalid_period` | 422 | Loan or renewal period must be at least one day |
| `loan.return_before_issue` | 422 | |
| `member.cannot_borrow` | 422 | Suspended, expired or cancelled |
| `member.loan_limit_reached` | 422 | At `MembershipType.MaxConcurrentLoans` |
| `copy.not_available` | 409 | Not issuable in its current status |
| `fine.not_found` | 404 | |
| `fine.already_paid` | 409 | |
| `fine.already_waived` | 409 | |
| `fine.not_overdue` | 422 | No fine is assessed for an on-time return |
| `fine.invalid_rate` | 422 | |
| `fine.waiver_reason_required` | 422 | |
| `validation.failed` | 422 | See the `errors` object |
| `auth.forbidden` | 403 | Phase 5 |
| `request.cancelled` | 499 | The client disconnected |
| `server.unexpected_error` | 500 | A bug. Details are in the log, never the response |

#### Request-level codes

Model binding fails before any action runs, so these never reach the domain.
They carry the same envelope as every other error - see **Every error carries a
code** below.

| Code | Status | Meaning |
|---|---|---|
| `request.body_required` | 400 | Body was empty |
| `request.malformed_json` | 400 | Body is not valid JSON |
| `request.unknown_property` | 400 | A property the endpoint does not accept. See `errors` |
| `request.type_mismatch` | 400 | Right shape, wrong type for a field |
| `request.invalid` | 400 | Query or route value could not be bound |
| `request.unsupported_media_type` | 415 | Missing or wrong `Content-Type` |
| `request.method_not_allowed` | 405 | |
| `request.not_acceptable` | 406 | |
| `route.not_found` | 404 | No route matched. Distinct from `<resource>.not_found` |
| `request.rate_limited` | 429 | |

**Every error carries a code.** Including the ones the framework generates
before our code runs - malformed JSON, a wrong `Content-Type`, an unmatched
route. Those used to fall through to ASP.NET Core's default ProblemDetails,
which has no `errorCode` at all, leaving a client told to branch on the code
with nothing to branch on for a whole family of responses. They also named
internal .NET types in their messages; those messages are now rewritten.

**On 500.** The response carries a generic message and the `traceId` — nothing
more. A leaked stack trace hands an attacker framework versions, file paths, and
often the schema; it is OWASP's *Security Misconfiguration* in its commonest
form. Outside Production only, the exception type and stack trace are attached as
extensions to save a trip to the logs while developing.

---

## Pagination

Every collection endpoint returns the same envelope:

```json
{
  "items": [ ... ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 15,
  "totalPages": 1,
  "hasPreviousPage": false,
  "hasNextPage": false
}
```

| Parameter | Default | Constraint |
|---|---|---|
| `page` | 1 | 1-based. Below 1 is clamped to 1 |
| `pageSize` | 20 | **Capped at 100**, silently |

**The cap is a security control**, not a convenience. Without it,
`?pageSize=1000000` is a one-request denial of service: the database materialises
a million rows and the serializer allocates them all. Clamping rather than
rejecting means a client asking for too much still gets a useful answer.

Offset paging (`Skip`/`Take`) is used because a librarian's catalogue screen
needs to jump to an arbitrary page and show a total. Its known weaknesses —
`OFFSET 100000` still walks 100,000 rows, and a row inserted mid-browse shifts
everything down a slot — are accepted deliberately. Keyset paging fixes both but
cannot jump to page 47 or report a total.

Results always carry a unique tiebreaker (`ThenBy(Id)`), because SQL guarantees
no ordering among rows tying on the sort key. Without it, page 2 could repeat or
skip rows that page 1 already showed.

---

## Endpoints

### `GET /api/books`

Search, filter, sort, and page the catalogue. All parameters optional.

| Parameter | Type | Notes |
|---|---|---|
| `search` | string | Free text across title, subtitle, ISBN |
| `isbn` | string | Exact match. Hyphens ignored |
| `author` | string | Partial match on first or last name |
| `categoryId` | int | |
| `genreId` | int | |
| `publisherId` | int | |
| `publishedFrom` / `publishedTo` | date | Inclusive |
| `language` | string | ISO 639-1 |
| `availableOnly` | bool | Only titles with ≥ 1 copy on the shelf |
| `sortBy` | string | `title` `isbn` `published` `category` `created` |
| `sortDir` | string | `asc` (default) or `desc` |
| `page` / `pageSize` | int | See pagination |

Filters combine with **AND**.

**`sortBy` is resolved against a whitelist.** An unrecognised value falls back to
`title` rather than erroring — an unknown sort field is a client bug, not an
attack worth a 400, and a sensibly ordered page is more useful than a failure.
The security property is unaffected either way: the caller's string is used to
*look up* a pre-built expression, never to build SQL. A SQL parameter can only
stand in for a value, never a column name, so `ORDER BY @p` is not valid SQL and
no escaping would make interpolation safe.

```bash
curl "http://localhost:5112/api/books?search=design&availableOnly=true&sortBy=published&sortDir=desc"
```

```json
{
  "items": [
    {
      "id": 1,
      "isbn": "9780201633610",
      "title": "Design Patterns",
      "subtitle": "Elements of Reusable Object-Oriented Software",
      "categoryName": "Software Engineering",
      "publisherName": "Addison-Wesley",
      "publishedOn": "1994-10-31",
      "authors": ["Erich Gamma", "Richard Helm", "Ralph Johnson", "John Vlissides"],
      "genres": ["Programming", "Software Design"],
      "totalCopies": 3,
      "availableCopies": 3,
      "isAvailable": true
    }
  ],
  "page": 1, "pageSize": 20, "totalCount": 5, "totalPages": 1,
  "hasPreviousPage": false, "hasNextPage": true
}
```

`authors` is ordered by credit position, not alphabetically — the `AuthorOrder`
column on the join table exists for exactly this.

**200** always. An empty `items` array is a successful search, not a 404.

---

### `GET /api/books/{id}`

Full detail including every physical copy.

**200** with `BookDetailDto` · **404** `book.not_found`

```json
{
  "id": 1,
  "isbn": "9780201633610",
  "isbnDisplay": "978-0-20-163361-0",
  "title": "Design Patterns",
  "description": "Catalogue of 23 classic object-oriented design patterns.",
  "categoryId": 5, "categoryName": "Software Engineering",
  "publisherId": 1, "publisherName": "Addison-Wesley",
  "publishedOn": "1994-10-31", "language": "en", "pageCount": 395,
  "authors": [
    { "id": 1, "fullName": "Erich Gamma", "authorOrder": 1, "role": null }
  ],
  "genres": [ { "id": 1, "name": "Programming", "slug": "programming" } ],
  "copies": [
    { "id": 1, "bookId": 1, "barcode": "LIB-001000",
      "status": 0, "condition": 0,
      "shelfLocation": "D-01-1", "acquiredOn": "2024-06-15", "isAvailable": true }
  ],
  "totalCopies": 3, "availableCopies": 3,
  "createdAt": "2026-09-12T13:53:04.1234567+00:00",
  "updatedAt": null
}
```

`status` and `condition` serialise as integers, matching the explicitly numbered
domain enums:

| `status` | | `condition` | |
|---|---|---|---|
| 0 | Available | 0 | New |
| 1 | OnLoan | 1 | Good |
| 2 | Lost | 2 | Fair |
| 3 | Damaged | 3 | Poor |
| 4 | Withdrawn | | |

---

### `GET /api/books/isbn/{isbn}`

Identical payload to `GET /api/books/{id}`. Hyphenated and unhyphenated forms
both work — non-digits are stripped before matching, the same normalisation the
domain applies on write.

```bash
curl "http://localhost:5112/api/books/isbn/978-0-13-235088-4"
curl "http://localhost:5112/api/books/isbn/9780132350884"   # identical result
```

**200** · **404** `book.not_found`

---

### `GET /api/books/{id}/copies`

**200** with an array, possibly empty · **404** `book.not_found`

An empty array means the title is catalogued but the library holds no physical
copy — genuinely different from the book not existing, which is why the endpoint
checks existence rather than returning `[]` for both.

---

### `GET /api/members`

Search, filter, sort and page. **200** always — an empty page is a valid answer ·
**422** if `joinedTo` is earlier than `joinedFrom`.

```
GET /api/members
    ?search=            free text across name, membership number and email
    &status=            Active | Suspended | Expired | Cancelled
    &membershipTypeId=  exact
    &joinedFrom=        &joinedTo=      ISO dates
    &canBorrowOnly=     true -> only members currently permitted to borrow
    &sortBy=            name | email | joined | status | type | number
    &sortDir=           asc | desc
    &page=1             &pageSize=20    (max 100)
```

`canBorrowOnly` is a convenience over `status=Active` that survives the
eligibility rule getting more complex. Phase 4 may well make borrowing depend on
unpaid fines, at which point this filter keeps meaning what it says and
`status=Active` would not.

Page, page size and an unknown `sortBy` are **clamped**, not rejected — a caller
asking for too much still gets a useful answer. An inverted date range is
**refused**, because there is nothing sensible to clamp it to and the empty page
it would otherwise return is indistinguishable from a real result.

```json
{
  "items": [
    {
      "id": 24,
      "membershipNumber": "MEM-2026-00024",
      "fullName": "Asha Nair",
      "email": "asha.nair@example.com",
      "phone": "+919825044556",
      "membershipTypeName": "Student",
      "status": "Active",
      "joinedOn": "2026-09-13",
      "canBorrow": true
    }
  ],
  "page": 1,
  "pageSize": 2,
  "totalCount": 1,
  "totalPages": 1,
  "hasPreviousPage": false,
  "hasNextPage": false
}
```

---

### `GET /api/members/{id}`

**200** · **404** `member.not_found`

---

### `GET /api/members/number/{membershipNumber}`

**200** · **404** `member.not_found`

The lookup a librarian actually performs — the number is printed on the card in
front of them, the surrogate id is not. Case-insensitive, so
`mem-2026-00024` resolves.

---

### `POST /api/members`

**201** · **409** `member.duplicate_email` · **422** validation or
`member.membership_type_not_found`

```json
{
  "fullName": "Asha Nair",
  "email": "asha.nair@example.com",
  "phone": "+91 98250 44556",
  "address": "22 MG Road, Bengaluru 560001",
  "membershipTypeId": 2,
  "joinedOn": "2026-09-13"
}
```

Note what is **absent**: `membershipNumber` and `status`. The number is derived
from the database key after insert and cannot be supplied; status is reachable
only through the transition endpoints. Sending either returns **400**
`request.unknown_property` rather than being ignored.

`joinedOn` is optional and defaults to today. It must fall between
**1900-01-01** and today inclusive — the join year is baked into the membership
number, which is immutable once issued, so a mis-keyed century is permanent.

**201** response — `Location: /api/members/24`:

```json
{
  "id": 24,
  "membershipNumber": "MEM-2026-00024",
  "fullName": "Asha Nair",
  "email": "asha.nair@example.com",
  "phone": "+919825044556",
  "address": "22 MG Road, Bengaluru 560001",
  "membershipTypeId": 2,
  "membershipTypeName": "Student",
  "maxConcurrentLoans": 8,
  "loanPeriodDays": 28,
  "status": "Active",
  "statusReason": null,
  "canBorrow": true,
  "joinedOn": "2026-09-13",
  "createdAt": "2026-09-13T16:10:38.2740692+00:00",
  "updatedAt": "2026-09-13T16:10:38.3622589+00:00"
}
```

The phone was sent as `+91 98250 44556` and stored as `+919825044556` — the value
object normalises, so formatting differences never become data differences. Email
is lower-cased for the same reason, which is what makes the unique index mean what
it appears to mean: `ASHA@EXAMPLE.COM` returns **409**, not a second row.

---

### `PUT /api/members/{id}`

**200** · **404** · **409** `member.duplicate_email` · **422**

Contact details and membership type only. Status is deliberately not settable
here — a routine correction to a phone number must not be able to restore
borrowing rights a librarian withdrew.

Re-saving a member with their own unchanged email returns **200**, not a false
409: the uniqueness check excludes the row being edited.

---

### Status transitions

Each is its own endpoint rather than a `status` field, because "suspend this
member, with this reason" is a different operation from "correct this member's
phone number".

| Route | Effect | Refuses |
|---|---|---|
| `POST /api/members/{id}/suspend` | `Suspended`, reason recorded | **422** without a reason · **409** `member.cancelled` |
| `POST /api/members/{id}/reactivate` | `Active`, reason cleared | **409** `member.cancelled` |
| `POST /api/members/{id}/expire` | `Expired` — lapsed by time, not conduct | **409** `member.cancelled` |
| `POST /api/members/{id}/cancel` | `Cancelled`, terminal | **409** `member.already_cancelled` |

All four return **404** `member.not_found` for an unknown id, and **200** with the
full member on success.

**Reactivate is idempotent; cancel is not.** Reactivating an already-active member
returns 200 and changes nothing — it carries no payload, so there is no caller
intent to discard. Cancelling an already-cancelled member returns **409**: the
call carries a *new* reason that cannot be honoured, and answering 200 while
discarding it would report a revision that did not happen. The closure reason may
document a data protection request, so it is written once.

Suspending an already-suspended member **does** revise the reason, and that is
deliberate: a suspension is reversible and ongoing, so its grounds can legitimately
change as fines accumulate. A cancellation is terminal and its reason is history.

```json
{ "reason": "Unpaid fine of Rs.150" }
```

Required for `suspend`, optional for `cancel`, not accepted by the other two.
Maximum 500 characters.

---

### `GET /api/membership-types`

**200** with an array. `memberCount` is counted in SQL — one query, not one per
type.

```json
[
  {
    "id": 2,
    "name": "Student",
    "description": "Longer loans for study; requires proof of enrolment.",
    "maxConcurrentLoans": 8,
    "loanPeriodDays": 28,
    "memberCount": 3
  }
]
```

---

### `GET /api/membership-types/{id}`

**200** · **404** `membership_type.not_found`

---

### `POST /api/membership-types`

**201** · **409** `membership_type.duplicate_name` · **422** validation

```json
{
  "name": "Community Partner",
  "description": "Local partner organisations",
  "maxConcurrentLoans": 4,
  "loanPeriodDays": 21
}
```

`maxConcurrentLoans` must be 1–50, `loanPeriodDays` 1–365, `name` 1–50 characters.

Names collide **case-insensitively**, and the conflict message quotes the spelling
already on file rather than the one sent — a librarian told
`'sTaNdArD' already exists` would go looking for a type that is not there under
that name:

```json
{
  "type": "https://httpstatuses.io/409",
  "title": "Request conflicts with the current state of the resource",
  "status": 409,
  "detail": "A membership type named 'Standard' already exists.",
  "instance": "POST /api/membership-types",
  "errorCode": "membership_type.duplicate_name",
  "traceId": "00-3560002ad0a5d5bbca03b02449d27b0f-fb730f63b849896e-00"
}
```

---

### `POST /api/loans/issue`

**201** · **404** copy or member · **409** `loan.copy_already_on_loan` /
`copy.not_available` · **422** `member.cannot_borrow`,
`member.loan_limit_reached`, validation

```json
{ "bookCopyId": 40, "memberId": 2 }
```

Note what is **absent**: `issuedAt` and `dueAt`. Both are the server's to decide —
the issue time comes from the clock, the due date from the member's
`MembershipType.LoanPeriodDays`. Accepting either would let a caller grant
themselves a longer loan than their membership allows, or back-date an issue to
avoid a fine.

**A 409 means the copy is out**, and gives the same answer whether it was already
out when the request arrived or was issued to someone else a moment earlier. The
filtered unique index `Loan(BookCopyId) WHERE ReturnedAt IS NULL` is what decides;
from the caller's side both mean *someone else has it*.

```json
{
  "type": "https://httpstatuses.io/409",
  "title": "Request conflicts with the current state of the resource",
  "status": 409,
  "detail": "Copy 'LIB-001039' is already on loan to MEM-2025-00002 until 2026-08-28.",
  "errorCode": "loan.copy_already_on_loan"
}
```

---

### `GET /api/loans`

**200** · **422** on an inverted date range

```
GET /api/loans
    ?search=          barcode, book title, member name, membership number
    &memberId=        &bookCopyId=   &bookId=
    &status=          Active | Overdue | Returned
    &overdueOnly=     true -> the chase list
    &issuedFrom=      &issuedTo=     &dueFrom=      &dueTo=
    &sortBy=          due | issued | returned | member | title | barcode
    &sortDir=         asc | desc
    &page=1           &pageSize=20   (max 100)
```

`status` is **computed against the server clock**, not stored — a loan becomes
overdue at midnight with nothing writing to its row, so a column would be stale.
The default sort is `due` ascending, which puts the most overdue loan first.

---

### `GET /api/loans/{id}`

**200** · **404** `loan.not_found`. Includes the fine, if one was assessed.

---

### `POST /api/loans/{id}/return`

**200** · **404** · **409** `loan.already_returned` · **422**
`loan.return_before_issue`

```json
{ "condition": "Poor" }
```

`condition` is optional, recorded on inspection at the desk. A copy returned in
`Poor` condition goes to `Damaged` rather than back onto the shelf.

The return time is the server's. Whoever sets it decides the fine, and a caller
who can set it can set it to the due date.

**If the copy is late, the fine is assessed by a handler that runs after this
request's transaction commits.** That ordering is deliberate — a rolled-back
return must never leave a fine behind — and it means the fine is written in a
separate transaction. Response for a copy 16 days late at ₹50/day:

```json
{
  "id": 2,
  "status": "Returned",
  "daysOverdue": 16,
  "fine": {
    "id": 1,
    "loanId": 2,
    "membershipNumber": "MEM-2025-00002",
    "daysOverdue": 16,
    "ratePerDay": 50,
    "amount": 800,
    "outstandingAmount": 800,
    "isSettled": false
  }
}
```

---

### `POST /api/loans/{id}/renew`

**200** · **404** · **409** `loan.already_returned` / `loan.overdue_cannot_renew`
· **422** out-of-range period

```json
{ "additionalDays": 14 }
```

Optional — defaults to the member's own loan period, which is what "renew" means
at a desk: a Student gets another 28 days where a Standard member gets 14. Capped
at 365, because unbounded renewal is indistinguishable from never returning the
book.

**Refused once overdue.** Renewing a late loan would erase a fine that has already
accrued, turning "return it late and renew" into a way of never paying.

---

### `GET /api/members/{id}/loans`

**200** · **404** `member.not_found`

Deferred from Phase 3, which had no `Loan` entity for it to return. Takes the same
filters as `GET /api/loans`. A member with no loans is an empty **200**; an
unknown member is a **404** — different answers a caller must be able to tell
apart.

---

### `GET /api/members/{id}/balance`

**200** · **404** `member.not_found`

```json
{
  "memberId": 2,
  "membershipNumber": "MEM-2025-00002",
  "totalOutstanding": 800,
  "unsettledFineCount": 1,
  "activeLoanCount": 1,
  "overdueLoanCount": 0,
  "maxConcurrentLoans": 8,
  "canBorrowMore": true
}
```

Every figure is aggregated in SQL. Note that `totalOutstanding` does **not**
currently block borrowing — the debt is reported, and whether it should stop a
loan is an open policy decision.

---

### `GET /api/fines`

**200** · **422** on an inverted date range

```
GET /api/fines
    ?memberId=
    &outstanding=   true -> neither paid nor waived; false -> settled only
    &assessedFrom=  &assessedTo=
    &page=1         &pageSize=20
```

---

### `GET /api/fines/{id}`

**200** · **404** `fine.not_found`

---

### `POST /api/fines/{id}/pay`

**200** · **404** · **409** `fine.already_paid` / `fine.already_waived`

Payment is **all-or-nothing**. Part payment would need an amount-paid column, a
rule for overpayment, and a decision about whether a partly-paid fine still blocks
borrowing — none of which is in scope, and all of which would be half-answered by
accepting an amount here.

`amount` does not move once settled — it is a record of what was charged. Only
`outstandingAmount` drops to zero.

---

### `POST /api/fines/{id}/waive`

**200** · **404** · **409** `fine.already_paid` / `fine.already_waived` · **422**
no reason

```json
{ "reason": "Hospitalised; produced documentation" }
```

The reason is required by the entity as well as the validator. Waiving money owed
is precisely the operation that has to be reviewable afterwards.

---

### Health

| Route | Checks | Purpose |
|---|---|---|
| `GET /health/live` | Process only — **not** the database | Liveness. Restarting because the database blipped turns a recoverable outage into an outage plus a restart loop |
| `GET /health/ready` | Includes the database | Readiness — can this instance serve traffic? |

Both return `200 Healthy` or `503 Unhealthy` with a plain-text body.

---
### `POST /api/books`

Catalogues a new book. The ISBN may be hyphenated; it is normalised and its check
digit verified.

```json
{
  "isbn": "978-1-59327-584-6",
  "title": "The Linux Command Line",
  "subtitle": "A Complete Introduction",
  "categoryId": 3,
  "publisherId": 2,
  "publishedOn": "2019-03-05",
  "language": "en",
  "pageCount": 504,
  "description": "A guide to the shell",
  "authorIds": [11, 5],
  "genreIds": [1, 5]
}
```

`authorIds` order becomes credit order. `membershipNumber`-style server-assigned
fields have no equivalent here, but note what is **absent**: `id`, `createdAt`.
Sending them is a `400` — see [Strict JSON](#strict-json) below.

**201** + `Location` · **409** `book.duplicate_isbn` · **422** validation, or a
referenced category/publisher/author/genre that does not exist
(`book.category_not_found`, `book.author_not_found` — all missing ids reported at
once).

---

### `PUT /api/books/{id}`

Full replacement of details, authors and genres. Sending the same body twice is
idempotent.

**The ISBN cannot be changed.** It is the natural key — one ISBN is one title —
so altering it would make this a different book. Delete and re-create instead.

**200** · **404** · **422**

---

### `DELETE /api/books/{id}`

**204** · **404** · **409** `book.has_copies_on_loan` — refused while any copy is
out, naming the barcodes. The cascade would otherwise delete a copy a member is
holding.

---

### `POST /api/books/{id}/copies`

```json
{ "barcode": "LIB-009001", "condition": "New", "shelfLocation": "T-16-1" }
```

Barcodes are unique **library-wide**, not per title.

**201** · **404** · **409** `copy.duplicate_barcode` · **422**

---

### `PUT /api/copies/{id}`

```json
{ "barcode": "LIB-009001", "condition": "Good", "shelfLocation": "T-16-2" }
```

All three fields are required — it is a `PUT`, so a full replacement.

**The barcode is editable**, so a damaged or unreadable label can be re-issued.
Delete-and-recreate would discard the copy's loan history. Uniqueness is checked
excluding the row being edited, so leaving the barcode unchanged does not conflict
with itself. Values are normalised to upper case.

**Status is deliberately not settable.** A copy becomes `OnLoan` by being issued
and `Available` by being returned; letting a client set it directly would allow a
copy to be marked available while a member still holds it.

**200** · **404** · **409** `copy.duplicate_barcode` · **422**

---

### `DELETE /api/copies/{id}`

**204** · **404** · **409** `copy.on_loan`

---

## Strict JSON

Every endpoint rejects JSON properties the request type does not declare:

```json
{
  "status": 400,
  "errors": {
    "$.id": ["The JSON property 'id' could not be mapped to any .NET member
              contained in type 'UpdateBookCopyRequest'."]
  }
}
```

**Why.** Request types are deliberately narrow — they exist to prevent mass
assignment (OWASP API6), so a caller cannot set `id`, `createdAt` or `status` by
posting a fuller object back. But `System.Text.Json` discards unknown members
*silently* by default, which produced a genuinely misleading API: a caller could
`PUT` a copy back with a changed `barcode`, receive `200`, and reasonably conclude
the barcode had changed. It had not — the DTO was right to ignore it; the `200`
was wrong.

This is stricter than most public APIs. It is the right default when you own both
ends of the contract.

---

## Import

### `POST /api/books/import`

`multipart/form-data`. The format is taken from the file extension: `.csv` or
`.txt` for CSV, `.json` for JSON.

| Parameter | Values | Default |
|---|---|---|
| `duplicates` | `Skip` · `Update` · `Fail` | `Skip` |

Expected CSV header — only `isbn`, `title` and `category` are required:

```
isbn,title,subtitle,category,publisher,publishedOn,language,pageCount,description,authors,genres
```

`authors` and `genres` are `;`-separated; author order becomes credit order.
Headers match case-insensitively and ignore underscores, so `PageCount`,
`pagecount` and `page_count` all bind.

Lookups are referenced **by name**, not id, and are created when absent — a
supplier's file cannot know this database's ids. `lookupsCreated` reports how many
were made; an unexpectedly high number means a mis-mapped column or a file full of
typos.

```json
{
  "totalRows": 10,
  "imported": 3,
  "updated": 0,
  "skipped": 2,
  "failed": 5,
  "lookupsCreated": 5,
  "errors": [
    { "lineNumber": 7, "isbn": "9780345539435",
      "errorCode": "import.invalid_isbn",
      "message": "ISBN check digit is invalid." }
  ],
  "hasErrors": true
}
```

**200 even when rows failed.** Partial success is the normal case: the request
succeeded and the rejected rows are data in the body. A 4xx would claim the upload
was wrong when only part of it was.

For CSV, `lineNumber` counts the header as line 1. For JSON it is the array index.

> **Malformed CSV imports its good rows; malformed JSON imports nothing.** The
> JSON reader buffers ahead, so a syntax error surfaces before the valid elements
> preceding it are yielded.

**Limits:** 20 MB, extension allow-list, `multipart/form-data` only.

**422** `import.file_required` · `import.file_too_large` ·
`import.unsupported_file_type` — a 422 rather than 400 because the multipart
request itself is well formed; it is the content the rules reject.

**413** if the body exceeds the framework limit before the handler runs.

**Row error codes:** `import.invalid_isbn`, `import.duplicate_in_file`,
`import.duplicate_isbn`, `import.title_required`, `import.category_required`,
`import.invalid_date`, `import.invalid_page_count`, `import.row_unreadable`.

---

### `GET /api/books/import/template`

A CSV template with the expected header and one worked example, generated from
the same field names the reader binds — so it cannot drift from what the importer
accepts.

**200**, `text/csv`, as a download.

---

## Reports

All four accept `?format=Csv|Json` except the summary, which is JSON only.
Exports stream: rows are written to the response as the database produces them,
so a large report starts downloading immediately and never exists in memory in
full.

> **CSV exports neutralise spreadsheet formula injection.** A value beginning
> `=`, `+`, `-` or `@` is prefixed with an apostrophe so Excel treats it as text
> rather than executing it. JSON exports deliberately do not — a spreadsheet never
> opens them, and prefixing would corrupt the data for every legitimate consumer.

CSV is UTF-8 **with** a BOM, so Excel on Windows reads non-ASCII names correctly.
Downloads are named `<report>-<yyyy-MM-dd>.<ext>`.

### `GET /api/reports/books/export`

| Parameter | Notes |
|---|---|
| `format` | `Csv` (default) or `Json` |
| `categoryId`, `publisherId` | |
| `availableOnly` | Only titles with a copy on the shelf |

Column names match the import template, so an exported catalogue can be edited in
a spreadsheet and imported straight back.

**200**, streamed as a download.

---

### `GET /api/reports/loans`

| Parameter | Notes |
|---|---|
| `from`, `to` | Filter on the **issue** date, inclusive of both days |
| `status` | `Active` · `Overdue` · `Returned` |
| `memberId` | |
| `format` | |

Status is evaluated as at now, so a loan currently late reports `Overdue` even if
it was within its term for most of the period.

**200** · **422** `report.invalid_date_range` when `to` is earlier than `from`.

---

### `GET /api/reports/overdue`

| Parameter | Notes |
|---|---|
| `asOf` | Defaults to today; a past date answers "who was overdue on the 1st?" |
| `format` | |

Carries each member's email and phone, because the purpose of this report is to
contact them.

`projectedFine` is what the fine **would** be if the copy came back on `asOf` —
`daysOverdue × ratePerDay`. Nothing is charged until the copy is actually
returned.

**200**, streamed as a download.

---

### `GET /api/reports/fines/summary`

JSON, not a file. Every figure is a SQL aggregate. Defaults to the current year
to date.

```json
{
  "from": "2026-01-01", "to": "2026-09-14",
  "totalFines": 1,
  "totalAssessed": 800.0,
  "totalPaid": 0.0,
  "totalWaived": 0.0,
  "totalOutstanding": 800.0,
  "paidCount": 0, "waivedCount": 0, "outstandingCount": 1,
  "totalOverdueDays": 16,
  "currency": "INR",
  "byMonth": [
    { "month": "2026-09", "count": 1, "assessed": 800.0, "outstanding": 800.0 }
  ]
}
```

**200** · **422** `report.invalid_date_range`

---

## Planned

### Phase 5, auth — deferred

Deliberately deferred behind Phases 6 and 7, neither of which depends on it. The
consequence is that **every endpoint currently ships unauthenticated**, including
the overdue report, which carries member email addresses and phone numbers.
Phase 8 revisits this regardless.

| Method | Route |
|---|---|
| `POST` | `/api/auth/register` · `/login` · `/refresh` · `/logout` |
| `GET` | `/api/loans/me` — the caller's own loans |

Bearer tokens; access token ~15 minutes, refresh token rotated on use and stored
hashed.

```
Authorization: Bearer <token>
```

Roles `Librarian` (full CRUD) and `Member` (own records only), enforced by policy
plus a resource-ownership handler.
