# Phase 6 — Import Books

**Goal:** load a supplier's catalogue file without holding it in memory, and
without letting one bad row cost the other 9,999.

---

## What we built

- `POST /api/books/import` — multipart CSV or JSON, `?duplicates=Skip|Update|Fail`
- `GET /api/books/import/template` — a CSV template generated from the reader's
  own field names
- `IBookImportReader` returning `IAsyncEnumerable<ImportRow<ImportedBookRow>>`,
  with CSV and JSON implementations
- `ILookupResolver` — turns names into ids, creating what is missing
- Per-row error reporting with line numbers and stable error codes
- 15 reader tests

---

## Concepts

### 1. Streaming, and why the obvious signature is wrong

The natural interface is:

```csharp
Task<List<ImportedBookRow>> ReadAsync(Stream source);   // DON'T
```

It reads the entire file before the caller sees row one. A 200 MB supplier feed
costs 200 MB of managed heap, most of it on the large object heap, and the
process is one concurrent upload away from failing.

```csharp
IAsyncEnumerable<ImportRow<ImportedBookRow>> ReadAsync(Stream source);   // DO
```

A row is read, processed, and becomes garbage before the next is parsed. Memory
stays roughly constant regardless of file size.

The controller completes the chain:

```csharp
await using Stream stream = file.OpenReadStream();
```

Not `CopyToAsync` into a `MemoryStream`, not a temp file. The stream goes
straight from the request body into the parser.

**What it costs.** The caller has to batch its own database writes, and a row
cannot be revisited once consumed — hence `seenInFile`, which tracks ISBNs
already accepted because the file cannot be re-read to check.

---

### 2. The reader takes a `Stream`, not an `IFormFile`

`IFormFile` is an ASP.NET Core type. Referencing it from `Library.Application`
would drag the web framework into a layer that has no business knowing HTTP
exists — rule 1.

The practical payoff is the test suite: all 15 reader tests run against a
`MemoryStream`, with no HTTP and no database. A reader coupled to `IFormFile`
would need a whole request to test a comma.

---

### 3. Partial success is the normal case

A 10,000-row file with four bad rows should import 9,996 books and name the four.
That shapes three decisions:

**The result reports counts and per-row errors** rather than throwing:

```json
{
  "totalRows": 10, "imported": 3, "skipped": 2, "failed": 5,
  "lookupsCreated": 5,
  "errors": [
    { "lineNumber": 7, "isbn": "9780345539435",
      "errorCode": "import.invalid_isbn",
      "message": "ISBN check digit is invalid." }
  ]
}
```

**The endpoint returns 200, not 4xx.** The request succeeded; the rejected rows
are data in the body. A 4xx would claim the upload was wrong when only part of it
was.

**The line number is carried from the reader onward.** "ISBN check digit is
invalid" is useless against a 10,000-row file. "Line 4,213: ISBN check digit is
invalid" is a fix. That is the only reason `ImportRow<T>` exists as a wrapper
rather than the reader yielding bare rows.

---

### 4. The one place this codebase catches `DomainException`

Controllers never catch — `GlobalExceptionHandler` maps exceptions centrally, so
one bad request becomes one error response. `ProcessRowAsync` is the exception:

```csharp
catch (DomainException ex)
{
    return RowOutcome.Failure(lineNumber, raw.Isbn, ex.ErrorCode, ex.Message);
}
```

**Here the unit of failure is a row, not the request.** A malformed ISBN on line
4,213 must cost that line and nothing else; letting it propagate abandons the
6,000 rows after it.

Note what is *not* caught. A `DbUpdateException` or an
`OperationCanceledException` means the import itself is in trouble, and
swallowing it per row would turn one real failure into 10,000 confusing row
errors. The catch is deliberately narrow, and the `ErrorCode` passes through
unchanged so the caller sees the same code the API would have returned.

---

### 5. `TryCreate` earns its place

`Isbn` has exposed both forms since Phase 2:

```csharp
public static Isbn Create(string? input);                              // throws
public static bool TryCreate(string? input, out Isbn, out string?);    // reports
```

`Create` is right for a single API request — one bad ISBN, one 422. `TryCreate`
is right here, where throwing would cost the batch. The non-throwing overload
existed for two phases before anything used it, which is the sort of speculative
API that usually ages badly — this one did not, because the bulk path was a known
requirement rather than a guess.

---

### 6. Lookups resolve by *name*, and creating is the default

A supplier's file says `Penguin Books` and `Science Fiction`. It cannot know this
database's ids and has no way to pre-register anything.

An importer that rejected every row whose publisher was unknown would fail the
entire first import from any new supplier — precisely when the feature is needed.
So `ILookupResolver` creates what is missing.

**The cost is real and named.** A typo creates a lookup row: `Pengiun Books`
becomes a publisher and nothing flags it. Three mitigations, none of which
eliminate it:

- names are trimmed and matched case-insensitively, so presentation differences
  do not create duplicates
- `lookupsCreated` is reported, so an unexpectedly large number reveals a
  mis-mapped column
- merging duplicates afterwards is a tractable admin job

Blocking the import would not prevent bad data, only delay it.

**The resolver is scoped and stateful.** It caches every name it resolves for the
life of the request — a supplier feed is mostly one publisher, and without the
cache a 10,000-row file issues 10,000 near-identical queries. The cache is also
what makes creation safe within one file: a new publisher on rows 5 and 6 is
created once, because row 6 finds the cached instance rather than querying a
database that has not committed yet.

---

### 7. Batching, and why each batch is two saves

```csharp
private const int BatchSize = 100;
```

One save per row is a round trip per book. One save at the end means EF Core's
change tracker holds every entity for the whole import, and its per-save fixup
work grows with the number tracked — the last rows run far slower than the first.

Each batch commits **twice**:

```csharp
await _unitOfWork.SaveChangesAsync(ct);   // books get their ids
// ... then set authors and genres ...
await _unitOfWork.SaveChangesAsync(ct);   // join rows
```

`BookAuthor` and `BookGenre` carry the book's key as half of their composite
primary key, and the database does not assign that key until the book row is
inserted. This is the same constraint that makes member registration two saves —
anything derived from a database-generated key costs a second round trip.

---

### 8. Strategy: three correct answers to one question

```csharp
public enum DuplicateHandling { Skip, Update, Fail }
```

Not a rule, because all three are right in different situations:

- **Skip** — a monthly feed that resends the whole catalogue. Most rows are known,
  and re-importing them would churn `UpdatedAt` on thousands of untouched books.
- **Update** — a corrected feed, where the incoming data is newer.
- **Fail** — a one-off load into what should be an empty catalogue. A duplicate
  means the file is wrong, and continuing would hide that.

---

### 9. Strict where we own both ends; lenient where we own neither

The API rejects unknown JSON properties (Phase 2's `UnmappedMemberHandling.Disallow`):
a caller sending a field we ignore has misunderstood a contract we control, and
should be told.

The import reader does the opposite:

```csharp
UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
```

A supplier's file carries their schema. Extra fields are expected, and rejecting
a file over one we do not need would make the feature unusable. The CSV reader
takes the same line — `MissingFieldFound = null`, headers matched
case-insensitively and ignoring underscores, so `PageCount`, `pagecount` and
`page_count` all bind.

These files come from spreadsheets and other people's exports. Rejecting correct
data over presentation helps nobody.

---

### 10. Upload limits, and what actually protects the system

```csharp
[RequestSizeLimit(MaxUploadBytes)]   // 20 MB, ~100,000 rows
[Consumes("multipart/form-data")]
```

Plus an extension allow-list. But the controller says plainly what that
allow-list is and is not:

> **The extension is a usability check, not a security boundary.** It is
> caller-supplied and trivially faked.

What protects the system is that the file is only ever *parsed* — never written
to disk, never executed, never used to build a path. A renamed executable
uploaded here fails to parse as CSV and is reported as an unreadable row.
`Path.GetFileName` strips any directory component, which matters even though
nothing is written, because the name is echoed in an error message.

---

## Things that bit us

### `yield return` is illegal inside a `catch`

Both readers want to catch a parse failure and yield an error row. C# forbids it
(`CS1631`), so each failure is captured into a local and yielded after the `try`
closes. It reads more awkwardly than the intent, and the comment in each reader
says so rather than leaving the next reader puzzled.

### A test asserted the wrong thing about JSON

The first version of `Malformed_json_reports_the_element_and_stops` asserted that
valid elements *before* a syntax error still arrive, as they do in CSV. It failed.

`DeserializeAsyncEnumerable` reads ahead in buffer-sized chunks, so a syntax error
anywhere surfaces **before** the preceding elements are yielded. The test was
wrong, not the code — and the difference is worth knowing before uploading a
large file:

| | Malformed input |
|---|---|
| **CSV** | Line-oriented. The bad line is reported; the rest still imports |
| **JSON** | Imports **nothing**. Fix the syntax and re-upload |

Both behaviours now have tests, so neither changes silently.

### `CA1716` reserves `For`

`IBookImportReaderFactory.For(format)` failed the build — `For` is a Visual Basic
keyword. Renamed to `GetReader`.

---

## SOLID in this phase

| Principle | Where |
|---|---|
| **S** | The reader parses; the service decides what is valid; the resolver resolves names. Three failures, three places |
| **O** | `BookImportReaderFactory` indexes readers by declared format — adding XML is one class and one registration, and the factory does not change |
| **L** | Both readers honour the same contract: stream, never buffer, report a bad row rather than throwing |
| **I** | `IBookImportReader` has two members. A reader knows nothing about books, duplicates, or the database |
| **D** | `Library.Application` declares the reader and resolver interfaces; CsvHelper and `System.Text.Json` live in Infrastructure |

---

## Verification

```bash
curl -F "file=@samples/books-import.csv" \
     "http://localhost:5112/api/books/import?duplicates=Skip"
```

Observed against a 10-row file with 5 deliberately bad rows:

| | |
|---|---|
| First run | `imported=3 skipped=2 failed=5 lookupsCreated=5` |
| Re-run (Skip) | `imported=0 skipped=5 failed=5` **`lookupsCreated=0`** |
| Re-run (Update) | `updated=5` |
| Re-run (Fail) | `failed=10` |
| JSON, 3 rows | `imported=2 failed=1`, unknown `supplierSku` ignored |

`lookupsCreated=0` on the second run is the meaningful one: it proves the
resolver found the existing rows rather than creating near-duplicates.

Errors carried the right line numbers — 6, 7, 8, 9, 10 — for a bad ISBN, a bad
check digit, an in-file duplicate, a missing ISBN, and a missing category.

---

## Questions you should be able to answer

1. Why `IAsyncEnumerable` rather than `Task<List<T>>`? What breaks at scale?
2. Why does the reader take a `Stream` rather than an `IFormFile`?
3. Why does the import return 200 when five rows failed?
4. Why does `ProcessRowAsync` catch `DomainException` when no controller does —
   and why does it catch nothing else?
5. Why is each batch two `SaveChanges` calls?
6. Why does the importer create missing lookups, and what does that cost?
7. Why is the JSON reader lenient about unknown fields when the API is strict?
8. Why does a malformed CSV import its good rows while a malformed JSON file
   imports nothing?
9. Why is the file-extension check explicitly *not* a security boundary?

---

## What Phase 7 builds on this

The export is this phase in reverse, and reuses the shape: `IAsyncEnumerable`
from the repository, a format-indexed factory, and a `Stream` at the boundary.
Export column names deliberately match the import template, so an exported
catalogue can be edited in a spreadsheet and imported straight back.
