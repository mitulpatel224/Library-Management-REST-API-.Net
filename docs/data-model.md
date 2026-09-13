# Data Model

The catalogue schema, how it was normalised, and why each index exists.

---

## Entity relationship diagram

```mermaid
erDiagram
    Category  ||--o{ Book      : "files"
    Category  ||--o{ Category  : "contains"
    Publisher ||--o{ Book      : "publishes"
    Book      ||--o{ BookCopy  : "has physical"
    Book      ||--o{ BookAuthor : ""
    Author    ||--o{ BookAuthor : ""
    Book      ||--o{ BookGenre  : ""
    Genre     ||--o{ BookGenre  : ""

    Category {
        int    Id PK
        string Name UK
        string Description
        int    ParentCategoryId FK "self-reference, nullable"
    }
    Publisher {
        int    Id PK
        string Name UK
        string Country
        string Website
    }
    Author {
        int      Id PK
        string   FirstName
        string   LastName
        DateOnly DateOfBirth
        string   Biography
    }
    Genre {
        int    Id PK
        string Name UK
        string Slug UK
    }
    Book {
        int      Id PK
        string   Isbn UK "13 digits, checksum validated"
        string   Title
        string   Subtitle
        int      CategoryId FK "RESTRICT"
        int      PublisherId FK "SET NULL, nullable"
        DateOnly PublishedOn
        string   Language
        int      PageCount
        string   Description
    }
    BookAuthor {
        int    BookId PK-FK
        int    AuthorId PK-FK
        int    AuthorOrder "credit position"
        string Role "Editor, Translator..."
    }
    BookGenre {
        int BookId PK-FK
        int GenreId PK-FK
    }
    BookCopy {
        int      Id PK
        int      BookId FK "CASCADE"
        string   Barcode UK "library-wide unique"
        int      Status "Available|OnLoan|Lost|Damaged|Withdrawn"
        int      Condition
        string   ShelfLocation
        DateOnly AcquiredOn
    }
```

Phase 4 adds `Loan` and `Fine`; Phase 5 adds `User`, `Role`, `UserRole`,
`RefreshToken`, `Member` and `MembershipType`.

---

## The central modelling decision: Book vs BookCopy

This is the one worth defending first, because everything else follows from it.

A **`Book`** is a bibliographic record — one row per ISBN. It carries the title,
the authors, the publisher, the genres.

A **`BookCopy`** is a physical object on a shelf, with its own barcode, its own
condition, and its own location.

A library holding three copies of *Clean Code* has **one** `Book` row and
**three** `BookCopy` rows.

**Why not one row per book, with an `IsAvailable` flag?** Because the moment the
library owns two copies, that flag cannot answer the question. Is the book
available? Partly. The flat model can represent "some copy is out" but not "which
one", cannot record that copy #2 is damaged while copies #1 and #3 circulate, and
has nowhere to put a barcode — the thing actually scanned at the desk.

Loans therefore point at a `BookCopy`, never at a `Book`. That single choice is
what makes the system's headline rule — *a copy already on loan cannot be issued*
— expressible at all.

The cost is a level of indirection on every endpoint that deals with
availability, and the fact that availability becomes a computed aggregate rather
than a column read. That is why the listing query counts copies in SQL rather
than loading them.

---

## Normalisation walkthrough

Starting from the shape most people write first, and removing one class of
anomaly at a time.

### Unnormalised

```
Books
─────────────────────────────────────────────────────────────────────────
Id │ Isbn          │ Title           │ Authors                    │ Genres
 1 │ 9780201633610 │ Design Patterns │ Gamma, Helm, Johnson, Vlis.│ Programming, Software Design
 2 │ 9780132350884 │ Clean Code      │ Robert C. Martin           │ Programming
```

Plus `PublisherName`, `PublisherCountry`, and `CategoryName` as text columns.

### 1NF — no repeating groups

**Violation:** `Authors` and `Genres` hold comma-separated lists. One cell, many
values.

**Why it hurts:** "find every book by Erich Gamma" becomes
`WHERE Authors LIKE '%Gamma%'`, which cannot use an index and also matches an
author named *Gammage*. Removing the third of four authors means parsing and
rewriting a string. There is nowhere to record that an author is the *editor*.

**Fix:** extract `Author` and `Genre` into their own tables, linked through
`BookAuthor` and `BookGenre` junctions. Each cell now holds exactly one value.

### 2NF — no partial dependency on part of a composite key

**Violation:** the `BookAuthor` junction has the composite key
`(BookId, AuthorId)`. Carrying `AuthorName` on that row makes it depend on
`AuthorId` alone — half the key.

**Why it hurts:** an author credited on twelve books has their name stored twelve
times. Correcting a spelling means twelve updates, and any row missed becomes a
second, phantom author in the filter dropdown.

**Fix:** the junction holds only the two keys plus attributes that genuinely
depend on *both* — `AuthorOrder` and `Role`. The name lives on `Author`.

### 3NF — no transitive dependency on a non-key column

**Violation:** `Book.PublisherCountry` depends on `PublisherName`, which is not a
key. Country is an attribute of the publisher, not of the book.

**Why it hurts:** the same update anomaly one level out. It also makes
"list all publishers" a `SELECT DISTINCT` over the books table, which invents a
publisher every time someone mistypes a name, and loses any publisher that has no
books yet.

**Fix:** extract `Publisher` (and `Category` by the same argument). `Book` holds
only foreign keys.

### Result

```
Book(Id, Isbn, Title, Subtitle, CategoryId→, PublisherId→,
     PublishedOn, Language, PageCount, Description)
```

Every non-key column now depends on the key, the whole key, and nothing but the
key.

---

## The one deliberate denormalisation

`BookCopy.Status` caches a fact the `Loan` table will already own: a copy is on
loan exactly when a loan row exists for it with no `ReturnedAt`. Storing it twice
means it can disagree with itself.

It is kept anyway, because catalogue search filters on availability constantly,
and a column read beats a correlated subquery on every listing query.

Three rules make it safe:

1. **Only the loan workflow writes it**, in the same transaction as the loan row.
2. The filtered unique index below — not the column — is the actual enforcement.
3. Any reconciliation report treats `Loan` as authoritative.

The column is an optimisation. The index is the truth.

---

## The invariant behind the core business rule

Arriving in Phase 4:

```sql
CREATE UNIQUE INDEX IX_Loan_ActiveCopy
  ON Loan(BookCopyId) WHERE ReturnedAt IS NULL;
```

A *partial* index in SQLite and PostgreSQL; a *filtered* index in SQL Server.
Both mean the same thing: uniqueness applies only to rows matching the predicate.
A copy may appear in many historical loan rows, but in **at most one** where
`ReturnedAt IS NULL`.

**Why this rather than a C# check.** The service does check for an active loan
first — that is what produces a helpful error message. But between that check and
the insert there is a window, and two concurrent requests can both pass it. Only
one can win a unique index. The loser's `SaveChanges` throws
`DbUpdateException`, which the service translates into `409 Conflict`.

> The check is for the error message. The index is the guarantee.

This is worth stating plainly because it is the difference between a rule that
holds under load and one that holds only in a demo.

---

## Index strategy

Twenty-four indexes, each with a job.

| Index | Table | Purpose |
|---|---|---|
| `IX_Books_Isbn` **UNIQUE** | Books | One ISBN is one title. Prevents double-cataloguing; makes ISBN lookup a seek |
| `IX_Books_Title` | Books | The default sort. Without it every page costs a full sort |
| `IX_Books_CategoryId` | Books | Category filter |
| `IX_Books_PublisherId` | Books | Publisher filter |
| `IX_BookCopies_Barcode` **UNIQUE** | BookCopies | A scanned barcode must identify one item library-wide |
| `IX_BookCopies_BookId_Status` | BookCopies | Composite, for the availability filter |
| `IX_BookAuthors_AuthorId` | BookAuthors | "Every book by author X" — the PK only covers the other direction |
| `IX_BookGenres_GenreId` | BookGenres | "Every book in genre Y" |
| `IX_Authors_LastName_FirstName` | Authors | Author search. **Not** unique — two people genuinely share a name |
| `IX_Categories_Name` **UNIQUE** | Categories | |
| `IX_Genres_Name`, `IX_Genres_Slug` **UNIQUE** | Genres | |
| `IX_Publishers_Name` **UNIQUE** | Publishers | |
| `IX_MembershipTypes_Name` **UNIQUE** | MembershipTypes | One name is one type. See the collation caveat below |
| `IX_Members_MembershipNumber` **UNIQUE** | Members | The number printed on the card must identify one member |
| `IX_Members_Email` **UNIQUE** | Members | One mailbox is one membership. Works because `Email` lower-cases on construction |
| `IX_Members_FullName` | Members | The default member sort. **Not** unique — two members genuinely share a name |
| `IX_Members_Status_MembershipTypeId` | Members | Composite, for the status and type filters |
| `UX_Loans_BookCopyId_Active` **UNIQUE, FILTERED** | Loans | `WHERE ReturnedAt IS NULL`. The lending invariant — see below |
| `IX_Loans_MemberId_ReturnedAt` | Loans | "what has this member got out?" |
| `IX_Loans_ReturnedAt_DueAt` | Loans | The overdue report: open loans, most late first |
| `UX_Fines_LoanId` **UNIQUE** | Fines | One fine per loan. Catches a replayed domain event |
| `IX_Fines_MemberId_Settlement` | Fines | "what does this member owe?" |

**On composite column order.** `IX_BookCopies_BookId_Status` is `(BookId, Status)`
and not the reverse, because a composite index can only be used left-to-right.
`BookId` is the equality predicate and is highly selective; `Status` has five
possible values. Reversed, the index would be nearly useless for
`WHERE BookId = @id AND Status = 0`.

**On the non-unique author index.** Making `(LastName, FirstName)` unique would
merge two different authors who happen to share a name, silently combining their
bibliographies. The index exists for search speed, not for identity.
`IX_Members_FullName` is non-unique for the same reason.

**On the filtered index — the one constraint that is load-bearing.**
`UX_Loans_BookCopyId_Active` is unique over only the rows where
`ReturnedAt IS NULL`. `Loan(BookCopyId)` cannot be unique outright, because a copy
is lent hundreds of times over its life; it is unique only among the loans that
are *currently open*.

This is the only place in the schema where a business rule is enforced by the
database rather than merely reflected in it. The application checks the same rule
twice for the sake of a readable error, but both checks READ before they WRITE, so
two concurrent requests can pass them both. The index serialises the writes and is
the actual guarantee.

Both providers support the feature — SQL Server calls it a "filtered index",
SQLite a "partial index" — but `HasFilter` takes RAW SQL that EF Core emits
verbatim, identifier quoting included. The filter text is therefore
provider-specific (`"ReturnedAt" IS NULL` vs `[ReturnedAt] IS NULL`) and is
applied in `LibraryDbContext.OnModelCreating` from the configured provider. An
unrecognised provider throws at startup rather than silently emitting an index
with no filter, which would reject the second loan of every copy.

**On `DateTimeOffset` under SQLite.** SQLite has no date type, and the provider
stores a `DateTimeOffset` as TEXT with its offset appended — so text ordering is
not chronological ordering and EF Core refuses to translate `ORDER BY` on such a
column at all. A SQLite-only value converter stores these as UTC `DateTime`
instead, whose lexicographic order is chronological. Nothing is lost: every
timestamp here comes from `IClock.UtcNow`, so the offset is always zero.

**On uniqueness and collation — a real, provider-dependent trap.** A unique index
is only as case-insensitive as its collation. SQL Server's default collation folds
case; SQLite's `BINARY` does not. So `IX_Members_Email` and
`IX_MembershipTypes_Name` do not behave identically across the two providers, and
the difference is invisible until someone sends the same value in different
casing.

`Email` sidesteps it entirely by lower-casing in the value object before the
value ever reaches the column — a binary index on already-normalised data is
case-insensitive for free, on every provider.

`MembershipTypes.Name` cannot use that trick, because the display casing has to
survive. It is instead compared with `UPPER()` on both sides in the repository,
which translates on both providers. Two residual gaps, stated rather than hidden:
SQLite's `UPPER()` folds ASCII only, and an application-level check cannot stop a
race between two concurrent inserts. Closing both needs `COLLATE NOCASE` on the
column, which makes the migration model provider-specific.

---

## Delete behaviours

Chosen per relationship. A blanket policy would be wrong in at least one place.

| Relationship | Behaviour | Reasoning |
|---|---|---|
| `Book → Category` | `RESTRICT` | Deleting a category must not silently delete every book filed under it. The delete fails and the librarian re-files first |
| `Book → Publisher` | `SET NULL` | A publisher can go out of business; the books remain, with no publisher |
| `BookCopy → Book` | `CASCADE` | A copy has no meaning without its title |
| `BookAuthor → Book/Author` | `CASCADE` | A join row is meaningless once either end is gone |
| `BookGenre → Book/Genre` | `CASCADE` | Same |
| `Category → Category` | `RESTRICT` | Deleting *Fiction* must not take *Science Fiction* with it |
| `Member → MembershipType` | `RESTRICT` | Deleting a membership type must not delete the members holding it. Re-assign them first |
| `Loan → BookCopy` | `RESTRICT` | Loan history is the record of who had what, and it outlives the copy |
| `Loan → Member` | `RESTRICT` | Same. This is why cancelling a membership retains the row rather than deleting it |
| `Fine → Loan` | `CASCADE` | A fine has no meaning without the loan that caused it |
| `Fine → Member` | `RESTRICT` | A member with an unpaid fine cannot be deleted out from under it |

`CASCADE` everywhere is the dangerous default: one careless delete of a popular
category would remove hundreds of books with no warning and no undo.

---

## Column dictionary

### Book

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | int identity | no | Surrogate PK |
| `Isbn` | nvarchar(13) | no | 13 digits, no hyphens. Unique. Checksum validated by the `Isbn` value object |
| `Title` | nvarchar(500) | no | |
| `Subtitle` | nvarchar(500) | yes | |
| `CategoryId` | int | no | FK, RESTRICT |
| `PublisherId` | int | yes | FK, SET NULL |
| `PublishedOn` | date | yes | `DateOnly` — no spurious time component |
| `Language` | nvarchar(10) | yes | ISO 639-1 |
| `PageCount` | int | yes | Must be > 0 if supplied |
| `Description` | nvarchar(4000) | yes | |
| `CreatedAt` / `UpdatedAt` | datetimeoffset | no / yes | Written centrally by `SaveChangesAsync`, never by a service |

`Isbn` is stored through a **private string backing field**, not an EF value
converter. A converter is applied to both sides of a comparison, so a `LIKE`
pattern such as `"%design%"` gets fed to the converter and throws at parameter
binding. The backing field keeps an ordinary indexable text column while the
domain still exposes a validated `Isbn`.

### BookCopy

| Column | Type | Null | Notes |
|---|---|---|---|
| `Id` | int identity | no | |
| `BookId` | int | no | FK, CASCADE |
| `Barcode` | nvarchar(50) | no | Unique **library-wide**. Upper-cased on creation |
| `Status` | int | no | Enum, explicitly numbered — see below |
| `Condition` | int | no | Enum |
| `ShelfLocation` | nvarchar(50) | yes | e.g. `A-12-3` |
| `AcquiredOn` | date | yes | |

**Enums are stored as `int` with explicit values:**

```csharp
public enum CopyStatus
{
    Available = 0, OnLoan = 1, Lost = 2, Damaged = 3, Withdrawn = 4,
}
```

The explicit numbering is not decoration. Letting the compiler assign values
means inserting a new member in the middle silently renumbers every value after
it — and reinterprets every existing row in the database.

Storing as `int` rather than `string` trades raw-SQL readability for the property
that renaming a C# member is not a breaking schema change.

---

## Member and MembershipType

### MembershipType

| Column | Type | Notes |
|---|---|---|
| `Id` | int, PK | |
| `Name` | nvarchar(50), **unique** | Compared case-insensitively; display casing preserved |
| `Description` | nvarchar(500), null | |
| `MaxConcurrentLoans` | int | 1–50. The limit Phase 4 checks before issuing |
| `LoanPeriodDays` | int | 1–365. The due date derives from this |

### Member

| Column | Type | Notes |
|---|---|---|
| `Id` | int, PK | Surrogate key. Internal |
| `MembershipNumber` | nvarchar(20), **unique** | `MEM-2026-00042`. Server-issued, immutable, printed on the card |
| `FullName` | nvarchar(200) | Indexed for the default sort |
| `Email` | nvarchar(256), **unique** | Stored lower-cased by the `Email` value object |
| `Phone` | nvarchar(16), null | Stored normalised — digits and a leading `+` only |
| `Address` | nvarchar(500), null | |
| `MembershipTypeId` | int, FK → MembershipTypes | `RESTRICT` on delete |
| `Status` | int | `MemberStatus`, persisted as its explicit ordinal; serialised as a **name** over HTTP |
| `StatusReason` | nvarchar(500), null | Why suspended or cancelled. Write-once for a cancellation |
| `JoinedOn` | date | Between 1900-01-01 and today. Feeds the membership number's year segment |

**Why `Status` is an int in the database and a string on the wire.** The column
stores the ordinal because `MemberStatus` numbers its members explicitly —
inserting a value in the middle would otherwise reinterpret every existing row.
The API sends the name because an ordinal is unreadable without the enum
definition beside it, and a client branching on the number breaks silently for
exactly the same reason the explicit numbering exists.

**Why the number and the key both exist.** The surrogate key is what foreign keys
point at; the membership number is what a librarian reads off a card. Deriving the
number from the key means registration saves twice — insert to obtain the key,
then update with the number — inside one transaction, so no member ever exists
without a number.

---

## Auditing

`CreatedAt` and `UpdatedAt` are populated in `LibraryDbContext.SaveChangesAsync`
for every `AuditableEntity`, using the injected `IClock`:

```csharp
case EntityState.Modified:
    entry.Entity.UpdatedAt = now;
    entry.Property(e => e.CreatedAt).IsModified = false;   // cannot be overwritten
    break;
```

Centralising it means it cannot be forgotten in the one place it matters, and
pinning `CreatedAt` guards against a caller overwriting the original creation
time by posting it back in an update payload.

---

## Seed data

15 books, 41 copies, created through the domain factory methods rather than
EF Core's `HasData`.

`HasData` bakes rows into the migration itself, so every change to the sample
data becomes a schema migration and every row needs a hard-coded primary key.
That is right for genuine reference data and wrong for demo content.

Running seed data through `Book.Create` and `Isbn.Create` has a second benefit
that proved itself immediately: the ISBN checksum validation **rejected a real
typo** in this project's own seed set on first run — `9780345539435` for *Cosmos*
should end in `4`. Data that bypassed validation would have populated a broken
row silently.
