---
name: api-contract-docs
description: REST conventions, status-code policy, and the ProblemDetails shape for this API — plus how to keep docs/api-contract.md and the phase guides in sync when endpoints change. Use when adding or changing any endpoint.
---

# API contract and documentation

---

## Status-code policy

| Code | When |
|---|---|
| `200` | Successful read or update. An empty collection is still 200 |
| `201` | Created. Include a `Location` header |
| `204` | Deleted. No body |
| `400` | Malformed — model binding rejected it |
| `401` | Unauthenticated |
| `403` | Authenticated, not permitted |
| `404` | Resource does not exist |
| `409` | Collides with current state |
| `422` | Well formed, but a rule says no |
| `500` | A bug. Generic message only |

### 400 vs 422 vs 409

The distinction gets conflated constantly. The test:

- **400** — the request could not be *understood*. `?page=abc`.
- **422** — understood, but the *request itself* is wrong. "Member already has 5
  books out." Retrying unchanged always fails.
- **409** — understood and valid, but collides with *current state*. "That copy
  is on loan." Retrying later, unchanged, may succeed.

> Could this succeed later without changing the request?
> **Yes → 409. No → 422.**

### Not-found vs empty

- `GET /api/books?search=zzzz` → **200** with `items: []`. A search that matched
  nothing succeeded.
- `GET /api/books/9999` → **404**. A specific resource was requested and does not
  exist.
- `GET /api/books/1/copies` on a book with no copies → **200** with `[]`, but
  **404** if book 1 does not exist. Hence the existence check in the service.

---

## Error shape

RFC 9457, `application/problem+json`, produced centrally by
`GlobalExceptionHandler`:

```json
{
  "type": "https://httpstatuses.io/404",
  "title": "Resource not found",
  "status": 404,
  "detail": "Book with identifier '9999' was not found.",
  "instance": "GET /api/books/9999",
  "errorCode": "book.not_found",
  "traceId": "00-67ac...-ba4c...-00"
}
```

**`errorCode` is the contract.** Stable, machine-readable, `resource.reason` in
snake_case. Clients branch on it; `detail` is prose and may be reworded.

Adding a new error code means:
1. Throwing it with that code in the domain or service.
2. Adding an arm to `GlobalExceptionHandler.Map` **if** it needs a status the
   base types do not already give.
3. Adding a row to the error-code table in `docs/api-contract.md`.

**Never leak internals.** A 500 returns a generic message and a `traceId`. Error
messages must not echo data the caller is not entitled to — a 404 is reachable
unauthenticated.

Validation failures add `errors`:

```json
"errors": { "Isbn": ["ISBN check digit is invalid."] }
```

---

## REST conventions

**Plural nouns; verbs are the HTTP method.** `/api/books`, never
`/api/getBooks`.

**Nest a sub-resource when the child has no independent meaning:**
`/api/books/{id}/copies` — a copy without its title is nothing.
Where the child *does* stand alone, give it a top-level route:
`DELETE /api/copies/{id}`.

**Route constraints** so a bad id is a 400 from routing, not a 404 from a
lookup: `[HttpGet("{id:int}")]`.

**Name every route** — `[HttpGet("{id:int}", Name = "GetBookById")]` — so
`CreatedAtRoute` can reference it and the OpenAPI operation id is stable.

**Dates** are ISO 8601. `DateOnly` for a date, `DateTimeOffset` for an instant.

---

## Collection endpoints

Always return the `PagedResult<T>` envelope:

```json
{ "items": [], "page": 1, "pageSize": 20, "totalCount": 0,
  "totalPages": 0, "hasPreviousPage": false, "hasNextPage": false }
```

- `page` 1-based, clamped to ≥ 1.
- `pageSize` default 20, **capped at 100** — a security control, not a
  convenience. Clamped silently rather than rejected.
- Filters combine with **AND**.
- `sortBy` is resolved against a whitelist; an unknown value falls back to the
  default rather than erroring.

Never return a bare `List<T>` from a collection endpoint. It works against 15
seeded rows and falls over against 200,000.

---

## Controller shape

```csharp
/// <summary>Gets one book by its id, including its physical copies.</summary>
/// <param name="id">The book's surrogate key.</param>
/// <param name="cancellationToken">Cancelled when the client disconnects.</param>
/// <response code="200">The book.</response>
/// <response code="404">No book with this id exists.</response>
[HttpGet("{id:int}", Name = "GetBookById")]
[ProducesResponseType<BookDetailDto>(StatusCodes.Status200OK)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
public async Task<ActionResult<BookDetailDto>> GetById(int id, CancellationToken ct)
    => Ok(await _bookService.GetByIdAsync(id, ct));
```

- `[ProducesResponseType]` for **every** outcome — it is what OpenAPI documents,
  and a missing one means a client generator produces the wrong types.
- XML comments become the Swagger description. Write them for the caller, not
  for yourself.
- No `try`/`catch`. No business logic.

For `201`:

```csharp
return CreatedAtRoute("GetBookById", new { id = created.Id }, created);
```

---

## OpenAPI in this solution

The document is produced by the **framework** (`AddOpenApi` / `MapOpenApi`) at
`/openapi/v1.json`. `Swashbuckle.AspNetCore.SwaggerUI` renders it at `/swagger`.

There is no `AddSwaggerGen` anywhere, and adding one would be wrong — the .NET 10
template dropped Swashbuckle *generation* deliberately; the framework generator
is trimming- and AOT-friendly.

Document metadata is set by a transformer in `OpenApi/OpenApiConfiguration.cs`.
Note `Microsoft.OpenApi` 2.x flattened its namespaces: `OpenApiInfo` is in
`Microsoft.OpenApi`, **not** `Microsoft.OpenApi.Models`.

---

## Keeping docs in sync

Changing an endpoint is not finished until these are updated:

| File | What changes |
|---|---|
| `docs/api-contract.md` | Endpoint table, parameters, example request/response, any new error code |
| `docs/phases/phase-NN.md` | The concept the change demonstrates, if new |
| `README.md` | The API-surface table, if an endpoint was added or removed |
| `TASKS.md` | Tick the item; add a Decisions Log row for any non-obvious choice |

Include a **real** example response — copy it from an actual call, do not invent
it. An example that does not match reality is worse than none, because it is
believed.

---

## Before calling an endpoint done

```bash
dotnet build LibraryManagement.slnx -c Release    # 0 warnings
dotnet run --project src/Library.Api
```

Then actually exercise it:

1. Happy path returns the documented shape.
2. Not-found returns 404 with the right `errorCode`.
3. Invalid input returns 422 with a per-field `errors` object.
4. Conflicts return 409.
5. It appears correctly in `/swagger` with all response types listed.
6. Watch the logged SQL — `Microsoft.EntityFrameworkCore.Database.Command` is at
   `Information` in development. Confirm one request is not producing twenty
   queries.

Compiling is not verifying.
