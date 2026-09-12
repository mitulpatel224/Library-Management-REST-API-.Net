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

| Code | When | Notes |
|---|---|---|
| `200 OK` | Successful read or update | An empty result set is still 200 |
| `201 Created` | Resource created | `Location` header points at it |
| `204 No Content` | Successful delete | No body |
| `400 Bad Request` | Malformed request | Model binding — a string where an int belongs |
| `401 Unauthorized` | Missing or invalid token | Phase 5. Means *unauthenticated* |
| `403 Forbidden` | Authenticated but not permitted | Phase 5 |
| `404 Not Found` | Resource does not exist | |
| `409 Conflict` | Collides with current state | Copy already on loan; duplicate ISBN |
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
| `validation.failed` | 422 | See the `errors` object |
| `auth.forbidden` | 403 | Phase 5 |
| `request.cancelled` | 499 | The client disconnected |
| `server.unexpected_error` | 500 | A bug. Details are in the log, never the response |

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

### Health

| Route | Checks | Purpose |
|---|---|---|
| `GET /health/live` | Process only — **not** the database | Liveness. Restarting because the database blipped turns a recoverable outage into an outage plus a restart loop |
| `GET /health/ready` | Includes the database | Readiness — can this instance serve traffic? |

Both return `200 Healthy` or `503 Unhealthy` with a plain-text body.

---

## Planned

### Phase 2, write side

| Method | Route | Success | Failure |
|---|---|---|---|
| `POST` | `/api/books` | 201 + `Location` | 409 duplicate ISBN · 422 validation |
| `PUT` | `/api/books/{id}` | 200 | 404 · 422 |
| `DELETE` | `/api/books/{id}` | 204 | 404 · 409 copies on loan |
| `POST` | `/api/books/{id}/copies` | 201 | 404 · 409 duplicate barcode |
| `DELETE` | `/api/copies/{id}` | 204 | 404 · 409 on loan |

### Phase 4, lending

| Method | Route | Success | Failure |
|---|---|---|---|
| `POST` | `/api/loans/issue` | 201 | **409 copy already on loan** · 422 loan limit reached |
| `POST` | `/api/loans/{id}/return` | 200 + fine if overdue | 404 · 409 already returned |
| `GET` | `/api/loans` | 200 | |
| `GET` | `/api/loans/me` | 200 — caller's own loans | 401 |
| `POST` | `/api/fines/{id}/pay` | 200 | 404 · 409 already paid |

### Phase 5, auth

`POST /api/auth/register | login | refresh | logout`. Bearer tokens; access token
~15 minutes, refresh token rotated on use and stored hashed.

```
Authorization: Bearer <token>
```

Roles `Librarian` (full CRUD) and `Member` (own records only), enforced by policy
plus a resource-ownership handler.
