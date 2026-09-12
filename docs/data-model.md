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

Fourteen indexes, each with a job.

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

**On composite column order.** `IX_BookCopies_BookId_Status` is `(BookId, Status)`
and not the reverse, because a composite index can only be used left-to-right.
`BookId` is the equality predicate and is highly selective; `Status` has five
possible values. Reversed, the index would be nearly useless for
`WHERE BookId = @id AND Status = 0`.

**On the non-unique author index.** Making `(LastName, FirstName)` unique would
merge two different authors who happen to share a name, silently combining their
bibliographies. The index exists for search speed, not for identity.

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
