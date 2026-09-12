---
name: csharp-conventions
description: C# and .NET 10 coding conventions for this solution — nullability, records, async, analyzer rules that fail the build, and the traps hit during development. Use when writing or reviewing any C# in this repository.
---

# C# conventions

The build runs with `TreatWarningsAsErrors` and `AnalysisLevel=latest-recommended`.
Several conventions below exist because an analyzer enforces them.

---

## Types

```csharp
public sealed class BookService : IBookService        // sealed by default
public sealed record BookSummaryDto { ... }           // records for DTOs
public readonly record struct Isbn { ... }            // value objects
public abstract class Entity { ... }                  // only when inheritance is designed for
```

- **`sealed`** unless the type is designed for inheritance. It documents intent
  and lets the JIT devirtualise calls.
- **`record`** for DTOs, requests, and domain events — immutable data with value
  equality, trivial to assert on in tests.
- **`class`** for entities — they have identity, not value equality.
- **`readonly record struct`** for small value objects: value equality, no
  allocation.

---

## Nullability

`<Nullable>enable</Nullable>` solution-wide.

```csharp
public string Title { get; private set; } = null!;   // EF sets it; never null in practice
public string? Subtitle { get; private set; }        // genuinely optional
```

`= null!` on an EF-populated required property is the accepted idiom: the
compiler cannot see that EF assigns it, and the alternative is making a
non-nullable column nullable in the model.

Never use `!` to silence a warning you have not reasoned about.

---

## Async

```csharp
public async Task<BookDetailDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
```

- Suffix `Async`.
- `CancellationToken` is the **last** parameter, always.
- Thread it all the way to the database call. When a client disconnects, the
  token cancels the command instead of letting it run to completion for a
  response nobody will read.
- Return the `Task` directly when there is nothing to add after the `await` —
  one fewer state machine:

```csharp
public Task<PagedResult<BookSummaryDto>> SearchAsync(BookSearchRequest r, CancellationToken ct)
    => _repository.SearchAsync(r, ct);
```

- `async void` never, except an event handler.
- `.Result` / `.Wait()` never — deadlock risk and it defeats the point.

---

## Logging — `[LoggerMessage]`, not `_logger.LogX`

Analyzer **CA1848** fails the build on the conventional form.

```csharp
public sealed partial class Thing            // partial: the generator writes the body
{
    private readonly ILogger<Thing> _logger;

    void DoWork() => LogStarted(id, name);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Started {Id} for {Name}")]
    private partial void LogStarted(int id, string name);
}
```

`_logger.LogInformation("Started {Id}", id)` boxes every value-type argument and
allocates a `params object[]` on **every** call — including calls where the level
is disabled and the message is discarded.

**CA1873 — hoist expensive arguments.** Even with the generated method:

```csharp
// FAILS CA1873: PathString -> string conversion at the call site
LogRequestFailed(status, code, httpContext.Request.Path, detail);

// Correct
string path = httpContext.Request.Path.Value ?? string.Empty;
LogRequestFailed(status, code, path, detail);
```

Event ID ranges used here: 1000s seeding, 4000s expected 4xx, 5000s faults.

---

## Collections

```csharp
private readonly List<BookCopy> _copies = [];                    // collection expression
public IReadOnlyCollection<BookCopy> Copies => _copies.AsReadOnly();

List<int> ids = [.. authorIds.Distinct()];                       // spread
```

Expose `IReadOnlyCollection<T>`, never the `List<T>` — otherwise a caller can
mutate the collection and bypass the entity's invariants.

---

## Pattern matching

```csharp
if (request.CategoryId is > 0)       { }
if (pageCount is <= 0)               { }
if (parentId is not null)            { }

PageSize = PageSize switch
{
    < 1           => DefaultPageSize,
    > MaxPageSize => MaxPageSize,
    _             => PageSize,
};
```

**Order matters in a `switch` expression** — the first match wins. Specific
exception types must precede a base-type catch-all.

---

## Records and `with`

```csharp
public PageRequest Normalize() => this with { Page = Page < 1 ? 1 : Page };
```

Non-destructive mutation. Note that `with` returns the **declared** type, so
subtype members are preserved but the static type needs a cast:

```csharp
BookSearchRequest normalized = (BookSearchRequest)request.Normalize();
```

---

## Analyzer rules that have actually fired here

| Rule | Trigger | Fix |
|---|---|---|
| **CA1000** | `static` member on a generic type | Move to a non-generic companion class with a generic *method* |
| **CA1848** | `_logger.LogX("...", args)` | `[LoggerMessage]` partial method |
| **CA1873** | Expensive argument evaluated at a logging call site | Hoist into a local first |
| **CA1707** | Underscores in member names | Disabled **for test projects only** — `Skip_is_derived_from_page_and_size` reads as a sentence in test output |
| **CS1587** | `///` XML comment on a local function | Use `//` — local functions are not valid XML doc targets |
| **CA1861** | Constant array argument | Fires in generated migrations; the folder is excluded as generated code |

**Do not suppress a rule to make a build pass.** Either fix the code, or — if the
rule genuinely does not apply, as with generated files — scope the exclusion
narrowly in `.editorconfig` and say why.

---

## Exceptions

```csharp
throw new NotFoundException("Book", id);                                 // 404
throw new ConflictException("copy.duplicate_barcode", "...");            // 409
throw new BusinessRuleViolationException("book.title_required", "...");  // 422
```

- Error codes are stable, snake-case, `resource.reason`. Clients branch on them.
- Messages are for humans and must never echo data the caller is not entitled to
  — a 404 is reachable unauthenticated.
- Never `catch` an exception only to rethrow it. `throw;` preserves the stack;
  `throw ex;` destroys it.
- Controllers have no `try`/`catch` at all.

---

## Time

**Never** `DateTime.Now` or `DateTimeOffset.UtcNow` in production code. Inject
`IClock`. Only `SystemClock` reads the real time.

`DateTimeOffset` for instants (carries an explicit offset); `DateOnly` for dates
with no meaningful time, such as a publication date.

---

## EF Core in queries

```csharp
_context.Books.AsNoTracking()                            // reads never track
    .Where(b => b.CategoryId == id)                      // conditional, per filter
    .Select(b => new Dto { ... })                        // project BEFORE materialising
    .ToListAsync(ct);
```

- `Select` before `ToListAsync` — prevents N+1 and over-fetching.
- `AnyAsync(...)` not `CountAsync(...) > 0` — it stops at the first hit.
- Aggregate in SQL (`.Count(c => ...)`), never over a loaded collection.
- Never interpolate user input into a query. Sorting resolves through
  `BookSortOptions.SortMap`.
- `IQueryable` never leaves the repository.

---

## XML documentation

`GenerateDocumentationFile` is on; CS1591 is suppressed, so every member does not
need a comment — but public API surface should have one, because it becomes the
Swagger description.

Explain **why**, not what:

```csharp
/// <remarks>
/// Falling back rather than rejecting is deliberate: an unknown sort field is a
/// client bug, not an attack worth a 400, and a sensibly ordered page is more
/// useful than an error. The security property is unaffected either way — the
/// value never reaches the query.
/// </remarks>
```

---

## File organisation

One public type per file, named after it. Exception: tightly-coupled small types,
such as the lookup EF configurations in `LookupConfigurations.cs`.

`using` order: `System.*`, then third-party, then `Library.*`, alphabetical
within each group.
