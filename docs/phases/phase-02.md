# Phase 2 — Book / Catalogue APIs

**Goal:** a normalised catalogue and a search endpoint that stays fast and safe
as the data grows.

> **Status:** read side complete. Write side (POST/PUT/DELETE) still to come.

---

## What we built

- Six entities plus two junctions, normalised to 3NF
- `Isbn` value object with ISBN-13 check-digit validation
- EF configurations: 8 tables, 14 indexes, per-relationship delete behaviour
- `InitialCatalogue` migration
- Seeder — 15 books, 41 copies, through domain factories
- `GET /api/books` with filter, sort, and paging
- `GET /api/books/{id}`, `/isbn/{isbn}`, `/{id}/copies`

---

## Concepts

### 1. Book vs BookCopy — the modelling decision everything follows from

A **`Book`** is a bibliographic record: one row per ISBN. A **`BookCopy`** is a
physical object with a barcode. Three copies of *Clean Code* = one `Book` row,
three `BookCopy` rows.

**Why not one row with an `IsAvailable` flag?** The moment the library owns two
copies the flag cannot answer the question. It can express "some copy is out" but
not which one, cannot record that copy #2 is damaged while #1 and #3 circulate,
and has nowhere to put the barcode actually scanned at the desk.

Loans point at a `BookCopy`, never at a `Book`. That single choice is what makes
the system's headline rule — *a copy already on loan cannot be issued* —
expressible at all.

**What it costs.** A level of indirection on every availability question, and
availability becomes a computed aggregate rather than a column read.

---

### 2. Value objects — making illegal states unrepresentable

```csharp
public readonly record struct Isbn
{
    public static Isbn Create(string? input);                     // throws
    public static bool TryCreate(string? input, out Isbn, out string? error);
}
```

**Why not `string Isbn`.** A string property can hold `"hello"`, an empty string,
or an ISBN with a bad check digit, and nothing stops it. Parse once at the
boundary into a type that *cannot* exist in an invalid state, and every later
layer stops re-checking.

**Entity vs value object.** A `Book` has identity — two books with the same title
are still two books. An `Isbn` is a value: two instances holding `9780132350884`
are interchangeable. Hence `record struct`: equality by value, no allocation.

**Normalisation on input.** `"978-0-13-235088-4"` and `"9780132350884"` are the
same ISBN. Hyphens are stripped on construction, so both produce the same stored
value — which is what lets the unique index actually catch duplicates.

**The check digit earns its keep.** ISBN-13 weights digits alternately 1 and 3;
the sum must be a multiple of 10. It catches a mistyped digit and most
transpositions. On this project's *first run* it rejected a real typo in the seed
data — `9780345539435` for *Cosmos* should end in `4`.

**Two constructors, deliberately.** `Create` throws — right for a single API
request. `TryCreate` reports — right for Phase 6 bulk import, where one bad row
should be reported rather than aborting a 10,000-row batch.

---

### 3. Rich entities, not anaemic ones

```csharp
public sealed class Book : AuditableEntity
{
    private Book() { }                          // EF only

    public string Title { get; private set; }

    private readonly List<BookCopy> _copies = [];
    public IReadOnlyCollection<BookCopy> Copies => _copies.AsReadOnly();

    public static Book Create(Isbn isbn, string title, int categoryId, ...) { /* validates */ }

    public BookCopy AddCopy(string barcode, ...)
    {
        if (_copies.Any(c => c.Barcode == barcode))
            throw new ConflictException("copy.duplicate_barcode", ...);
        // ...
    }
}
```

Three deliberate choices:

- **Private constructor** — application code cannot create a `Book` in an invalid
  state; it must go through `Create`. EF Core still materialises entities by
  reflection.
- **`private set`** — state changes go through named methods, each enforcing its
  own rule. An anaemic model with public setters has nowhere to put "you cannot
  withdraw a copy that is on loan".
- **`IReadOnlyCollection` over a private list** — a caller cannot
  `book.Copies.Add(...)` and bypass the duplicate-barcode check.

EF Core writes to the private list directly via backing-field access, which is
how encapsulation survives persistence:

```csharp
builder.Metadata.FindNavigation(nameof(Book.Copies))!
    .SetPropertyAccessMode(PropertyAccessMode.Field);
```

---

### 4. Many-to-many with payload

`BookGenre` is a pure join — EF Core could generate it implicitly. `BookAuthor`
carries `AuthorOrder` and `Role`, so it is declared explicitly.

> The moment a relationship has attributes of its own, it is an entity.

**Why `AuthorOrder` matters.** Authorship order is meaningful — "Gamma, Helm,
Johnson, Vlissides" is not the same credit as any permutation — and a plain join
table has no inherent ordering. The database is free to return rows however it
likes, so without an explicit column the credit list would be arbitrary.

The composite primary key `(BookId, AuthorId)` is what makes crediting the same
author twice on one book impossible. Adding a surrogate `Id` would permit exactly
that.

---

### 5. `IQueryable` and deferred execution

This is the concept that separates code that scales from code that does not.

```csharp
IQueryable<Book> query = _context.Books.AsNoTracking();

if (!string.IsNullOrWhiteSpace(request.Search)) query = query.Where(...);
if (request.CategoryId is > 0)                  query = query.Where(...);
if (request.AvailableOnly)                      query = query.Where(...);

int totalCount = await query.CountAsync(ct);    // <- first SQL executes HERE
```

Nothing touches the database until `CountAsync`. Each `Where` appends to an
**expression tree**, which EF Core then translates to SQL.

**Watch the `if` statements.** A filter that was not supplied contributes no SQL
at all. An unfiltered listing generates a clean
`SELECT ... ORDER BY ... LIMIT` — not a query padded with
`(@p IS NULL OR col = @p)` clauses that defeat index use.

**The failure mode this avoids:**

```csharp
var books = _context.Books.ToList();                 // loads EVERY row
var filtered = books.Where(b => b.CategoryId == id); // filters in memory
```

Identical results, and it works fine against 15 seeded rows. Against 200,000 it
transfers the entire table over the wire on every request.

---

### 6. Projection — where N+1 is prevented

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

`Select` **before** `ToListAsync`, so EF translates the shape into the SQL column
list and joins. Two consequences:

- **No N+1.** Author names arrive with the page. Materialising first and mapping
  afterwards produces 1 query for the page + 20 for authors + 20 for genres.
- **No over-fetching.** `Description` and `PageCount` are never read, because the
  summary DTO does not mention them.

`AvailableCopies` is counted **in SQL**. The domain has a `Book.AvailableCopies`
property, but it counts a *loaded* collection — using it here would load all 41
copies to display a number.

**`AsNoTracking()`** because this is a read. Change tracking has EF snapshot every
entity to detect later edits — pure overhead for data about to be projected and
discarded.

---

### 7. DTOs — three reasons, only one cosmetic

1. **The contract stops being hostage to the schema.** Renaming a column becomes a
   mapping change, not a breaking change for every client.
2. **Serialising an entity leaks whatever is attached.** Navigation properties
   drag in related graphs, and cycles (`Book → Copies → Book`) either throw or
   emit enormous payloads.
3. **Projecting to a DTO changes the generated SQL** — see above.

Only the first is about tidiness.

---

### 8. Sort whitelisting — a security control, not a style choice

```csharp
public static IReadOnlyDictionary<string, Expression<Func<Book, object?>>> SortMap { get; } =
    new Dictionary<string, Expression<Func<Book, object?>>>(StringComparer.OrdinalIgnoreCase)
    {
        ["title"]     = book => book.Title,
        ["published"] = book => book.PublishedOn,
        // ...
    };
```

**The vulnerability this closes.** The obvious implementation interpolates:

```csharp
query.OrderBy($"{request.SortBy} {request.SortDir}");                  // dynamic LINQ
context.Books.FromSqlRaw($"SELECT * FROM Books ORDER BY {sortBy}");    // worse
```

Both hand the caller control of the SQL. **Parameterisation cannot save you
here** — a parameter stands in for a *value*, never an identifier or keyword, so
`ORDER BY @p` is not valid SQL and there is no escaping trick that makes it safe.

The whitelist turns an open-ended injection surface into a closed set of five
choices. The caller's string is used to *look one up*, never to build a query.

**Expression trees, not compiled delegates.** `Expression<Func<...>>` can be
inspected and translated to SQL, so the database sorts. A compiled `Func<...>`
would force every matching row into memory first — correct results, ruinous
performance.

**Falling back rather than rejecting.** An unknown sort key is a client bug, not
an attack worth a 400, and a sensibly ordered page beats an error. The security
property is unaffected either way.

Verify it yourself:

```bash
curl "http://localhost:5112/api/books?sortBy=Title;DROP%20TABLE%20Books--"
# 200 OK, sorted by title
```

---

### 9. Paging, and why the tiebreaker is not optional

```csharp
return ordered.ThenBy(b => b.Id);
```

SQL guarantees **no ordering** among rows that tie on the `ORDER BY` key. With
fifty books published in 2024 and `sortBy=published`, the database may return
them in any order — and a *different* order on the next call. Page 2 would then
repeat or skip rows page 1 already showed.

A unique tiebreaker makes the total ordering deterministic and paging stable.

**`pageSize` is capped at 100 server-side.** Without a ceiling,
`?pageSize=1000000` is a one-request denial of service. Clamped silently rather
than rejected, because a client asking for too much should still get an answer.

**Offset paging, deliberately.** `Skip`/`Take` is right for a catalogue screen
that must jump to an arbitrary page and show a total. Its weaknesses are known
and accepted: `OFFSET 100000` still walks 100,000 rows, and a mid-browse insert
shifts everything down a slot. Keyset paging fixes both but cannot jump to page
47 or report a total.

**The count query runs separately**, against the filtered query but *without*
ordering or paging — that is what makes `totalCount` mean "matches" rather than
"matches on this page".

---

### 10. Delete behaviour, chosen per relationship

| Relationship | Behaviour | Why |
|---|---|---|
| `Book → Category` | `RESTRICT` | Deleting a category must not silently delete every book filed under it |
| `Book → Publisher` | `SET NULL` | A publisher can fold; the books remain |
| `BookCopy → Book` | `CASCADE` | A copy has no meaning without its title |
| junctions | `CASCADE` | A join row is meaningless once either end is gone |

`CASCADE` everywhere is the dangerous default: one careless delete of a popular
category removes hundreds of books with no warning and no undo.

---

### 11. Service layer — translating "not there" into "404"

```csharp
// Repository reports what IS:
Task<BookDetailDto?> GetByIdAsync(int id, CancellationToken ct);

// Service decides what a miss MEANS:
return book ?? throw new NotFoundException("Book", id);
```

Different questions. Returning `null` and having every controller remember to
check works right up until one forgets — and then a `NullReferenceException`
becomes a 500 where a 404 was correct.

Note the asymmetry: an **empty search result is 200**, not 404. Only a request
for a *specific* resource can be "not found".

`GetCopiesAsync` checks existence first for the same reason — an empty array must
mean "catalogued but no copies held", not "no such book".

---

## The bug that shaped the design

**Symptom.** `GET /api/books?search=design` returned 500:

```
The LINQ expression 'EF.Functions.Like(b.Isbn.Value, ...)' could not be translated.
```

then, after a first fix attempt:

```
System.InvalidCastException: Invalid cast from 'System.String'
to 'Library.Domain.ValueObjects.Isbn'.
```

**Cause.** `Isbn` was mapped with an EF value converter:

```csharp
builder.Property(b => b.Isbn)
    .HasConversion(isbn => isbn.Value, value => Isbn.FromTrustedValue(value));
```

A converter makes the whole `Isbn` object the mapped property, and EF applies the
conversion to **both sides** of a comparison. Equality survives that — the
constant converts cleanly. A `LIKE` does not: EF tried to convert the *pattern*
`"%design%"` into an `Isbn` and threw at parameter binding.

`EF.Property<string>(b, nameof(Book.Isbn))` did not help either — the property
still carries the `Isbn` type mapping.

**Fix.** Drop the converter. Store a private `string` backing field and wrap it:

```csharp
private string _isbn = null!;
public const string IsbnPropertyName = "_isbn";

public Isbn Isbn
{
    get => ValueObjects.Isbn.FromTrustedValue(_isbn);
    private set => _isbn = value.Value;
}
```

```csharp
builder.Property<string>(Book.IsbnPropertyName).HasColumnName("Isbn").HasMaxLength(13);
builder.Ignore(b => b.Isbn);
```

Queries then address the plain column:

```csharp
EF.Functions.Like(EF.Property<string>(b, Book.IsbnPropertyName), $"%{term}%")
```

**Why this is better, not merely a workaround.** The database now sees an ordinary
indexable text column that `LIKE`, ranges and index seeks all treat normally,
while the domain still hands out a validated `Isbn` that cannot hold a malformed
value. Both halves are preserved; the converter was giving up the first to get the
second.

**Lesson.** A value converter is right for a type the database can only store one
way and you only ever compare for equality. It is the wrong tool when you need to
*search within* the value.

---

## SOLID in this phase

| Principle | Where |
|---|---|
| **S** | `ApplyFilters`, `ApplySorting`, projection are separate methods |
| **O** | `SortMap` — a new sort field is a dictionary entry, not a query change |
| **L** | `IBookRepository` → `BookRepository`, substitutable in tests |
| **I** | Five focused reads, not a generic `IRepository<T>` |
| **D** | Application declares `IBookRepository`; Infrastructure implements it |

---

## Verification

```bash
curl "http://localhost:5112/api/books?search=design"          # 5 results
curl "http://localhost:5112/api/books?author=Fowler"          # Refactoring
curl "http://localhost:5112/api/books?sortBy=published&sortDir=desc"
curl "http://localhost:5112/api/books/isbn/978-0-13-235088-4" # hyphens ignored
curl "http://localhost:5112/api/books?pageSize=99999"         # clamped to 100
curl "http://localhost:5112/api/books/9999"                   # 404 ProblemDetails
curl "http://localhost:5112/api/books?search=Design%20Patterns" | grep authors
# ["Erich Gamma","Richard Helm","Ralph Johnson","John Vlissides"] — credit order
```

Watch the generated SQL: `Microsoft.EntityFrameworkCore.Database.Command` is at
`Information` in development, so every statement is logged. Confirm that one page
request produces **two** queries, not twenty-one.

---

## Questions you should be able to answer

1. Why split `Book` from `BookCopy`? What becomes impossible without it?
2. When does `IQueryable` actually hit the database?
3. Why does `Select` before `ToListAsync` matter?
4. Why can't parameterisation prevent injection in `ORDER BY`?
5. Why `Expression<Func<T, object>>` rather than `Func<T, object>` in the sort map?
6. Why does paging need a unique tiebreaker?
7. Why is `RESTRICT` right for Category and `CASCADE` right for BookCopy?
8. Why does an empty search return 200 but a missing book return 404?
9. Why did the ISBN value converter break `LIKE`, and why is the backing field better?
10. Why is `AvailableCopies` counted in SQL rather than via `Book.AvailableCopies`?

---

## Still to do

- `POST` / `PUT` / `DELETE /api/books`
- Copies sub-resource writes
- FluentValidation validators
- Lookup endpoints for authors, genres, categories, publishers
- Unit and integration tests for the write paths
