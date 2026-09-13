---
name: domain-modelling
description: How entities, value objects, aggregates, and domain events are built in Library.Domain — private constructors, static factories, invariant enforcement, collection encapsulation, and why the domain references nothing. Use when adding or changing anything under Library.Domain.
---

# Domain modelling

`Library.Domain` **references nothing**. No EF Core, no ASP.NET Core, no NuGet
package at all. If something here seems to need one, define an interface in
Application and implement it in Infrastructure.

That constraint is the point: the domain is the one layer whose correctness does
not depend on a database being present, which is why every rule worth testing
lives here.

For where a domain type sits relative to the other layers, see
`dotnet-clean-architecture`. This skill is about what goes *inside* the types.

---

## Entity or value object?

| | Entity | Value object |
|---|---|---|
| Identity | Has one. Two rows with the same id are the same thing | None. Two instances with the same contents are interchangeable |
| Equality | By `Id` | By value |
| C# form | `sealed class : Entity` / `AuditableEntity` | `readonly record struct` |
| Mutable | Through methods only | Never |
| Examples | `Book`, `BookCopy`, `Member`, `Loan` | `Isbn`, `Email`, `PhoneNumber`, `Money` |

Two books with the same title are still two books — identity. Two `Isbn`s holding
`9780132350884` are the same ISBN — value.

---

## The entity shape

Every entity follows this, without exception:

```csharp
public sealed class Book : AuditableEntity
{
    private Book() { }                       // EF materialisation only

    private Book(Isbn isbn, string title, ...)   // called only by the factory
    {
        Isbn = isbn;
        Title = title;
    }

    public string Title { get; private set; } = null!;

    private readonly List<BookCopy> _copies = [];
    public IReadOnlyCollection<BookCopy> Copies => _copies.AsReadOnly();

    public static Book Create(Isbn isbn, string title, int categoryId, ...)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new BusinessRuleViolationException("book.title_required", "Book title is required.");
        }

        return new Book(isbn, title.Trim(), categoryId, ...);
    }

    public void UpdateDetails(string title, ...) { /* re-validates, then assigns */ }
}
```

Each part earns its place:

**Private parameterless constructor** — EF needs one to materialise a row. It is
private so application code cannot reach it and create a `Book` with no title.

**`private set` on every property** — there is no public setter to assign, so an
entity cannot be put into an invalid state from outside even if a DTO were
bypassed. It is also the second layer of the mass-assignment defence.

**Static factory, not a public constructor** — a constructor cannot be named and
cannot fail gracefully. `Book.Create` validates first and either returns a valid
`Book` or throws; there is no path to a half-built one. It also leaves room for
`Book.CreateFromImport` later with different rules, which an overloaded
constructor could not express.

**`= null!` on required reference properties** — the accepted idiom for something
EF assigns that the compiler cannot see. Never use `!` to silence a warning you
have not reasoned about.

**Private backing list, read-only projection** — `IReadOnlyCollection<T>`, never
the `List<T>`. Handing out the list lets a caller `Add` past every invariant the
entity enforces.

---

## Mutation goes through methods

Not property setters, and not a caller mutating the collection:

```csharp
public void AddCopy(BookCopy copy) { ... }
public void MoveUnder(int? parentCategoryId) { ... }
```

Name them for what happens in the business, not for the field they assign:
`MoveUnder`, not `SetParentCategoryId`. `Loan.Return`, not `SetReturnedAt`.

Every mutating method re-validates. `UpdateDetails` repeats the checks from
`Create` because "a book always has a title" is an invariant, not a
creation-time-only condition.

---

## Value objects

`Isbn` is the reference implementation.

```csharp
public readonly record struct Isbn
{
    private Isbn(string value) => Value = value;       // private ctor
    public string Value { get; }

    public static Isbn Create(string? input);                              // throws
    public static bool TryCreate(string? input, out Isbn isbn, out string? error);  // does not
    public static Isbn FromTrustedValue(string value);                     // skips validation
}
```

**Three entry points, three purposes.** `Create` for the normal path — invalid
input is a `BusinessRuleViolationException`. `TryCreate` for bulk import, where a
bad row should be *reported* rather than abort the batch, and for validators that
need the answer without the exception. `FromTrustedValue` for rehydrating a value
already validated on the way in — re-running a checksum on every materialised row
buys nothing.

**Normalise on construction.** `978-0-13-235088-4` and `9780132350884` are the
same ISBN, so hyphens are stripped. This is what lets a unique index actually
catch duplicates; storing the display form would let one book be added twice
under two spellings. Keep display formatting as a separate method
(`ToDisplayString()`), never as the stored value.

**`readonly record struct`** — value equality for free, no heap allocation. Use a
`sealed record` class instead only when the value is large or genuinely nullable
as a whole.

### Persisting a value object

Map it through a **private string backing field**, not an EF value converter:

```csharp
private string _isbn = null!;
public const string IsbnPropertyName = "_isbn";

public Isbn Isbn
{
    get => ValueObjects.Isbn.FromTrustedValue(_isbn);
    private set => _isbn = value.Value;
}
```

A converter makes the whole object the mapped property, and EF then applies the
conversion to *both* sides of a comparison. Equality survives that; `LIKE` does
not — EF tries to convert the pattern `"%design%"` into an `Isbn` and throws
`InvalidCastException` at parameter binding. A plain column keeps `LIKE` and
index seeks working while the domain still hands out a validated type.

Queries address the field by name via `EF.Property<string>(b, Book.IsbnPropertyName)`,
which is what the constant exists to keep in one place.

---

## Aggregates

An aggregate is the unit of consistency: one root, and everything whose
invariants the root is responsible for.

| Aggregate root | Owns | References by id |
|---|---|---|
| `Book` | `BookCopy`, `BookAuthor`, `BookGenre` | `CategoryId`, `PublisherId` |
| `Member` | its own state | `MembershipTypeId` |
| `Loan` | `Fine` | `BookCopyId`, `MemberId` |

Rules:

- **Mutate an aggregate only through its root.** Adding a copy goes through
  `book.AddCopy(...)`, so the invariant lives in one place.
- **Reference other aggregates by id, never by navigation-for-mutation.** A book
  holds an author's id; it is not responsible for that author's data.
- **One transaction, one aggregate changed** wherever possible. `IUnitOfWork`
  gives one transaction per use case.

`Book.AvailableCopies` counts the loaded collection, so it is only meaningful
when copies were eagerly loaded. Listing queries project the count in SQL
instead — otherwise displaying a page of 20 titles loads every copy of each.
Computed properties on an entity are for the entity's own use; read models get
their numbers from the database.

---

## Enforcing an invariant — which exception

| Situation | Throw | HTTP |
|---|---|---|
| A rule of the model forbids it | `BusinessRuleViolationException` | 422 |
| It collides with existing state | `ConflictException` | 409 |
| The thing does not exist | `NotFoundException` | 404 |

Always with a stable snake-case `ErrorCode` of the form `resource.reason`:
`book.title_required`, `category.cannot_parent_itself`,
`loan.copy_already_on_loan`. Clients branch on the code; humans read the message.

Messages must never echo data the caller may not be entitled to — a 404 is
reachable unauthenticated, so "Book with identifier '42' was not found" is fine
and a title or an email address is not.

Entities cannot check state they cannot see. "Is this ISBN already used?" needs
the database, so it belongs in the service, backed by a unique index. Keep the
entity's checks to what is knowable from its own fields and arguments.

---

## Domain events

Entities on `AuditableEntity` **collect** events; they do not dispatch them.

```csharp
public void Return(DateTimeOffset returnedAt)
{
    ReturnedAt = returnedAt;
    RaiseDomainEvent(new LoanReturnedEvent(Id, MemberId, returnedAt));
}
```

Infrastructure dispatches them **after `SaveChangesAsync` succeeds**, then calls
`ClearDomainEvents()`.

**Why not a C# `event`?** A C# `event` invokes its subscribers synchronously, at
the moment it is raised — which here is *before* the transaction commits. If the
transaction then rolled back, a fine would have been assessed for a return that
never happened. The two mechanisms solve different problems, and this project
demonstrates both deliberately: domain events for post-commit facts, a real C#
`event` on the Phase 4 notification service where fire-and-forget in-process
notification is the correct semantic.

Conventions:

- **Past tense.** `LoanReturnedEvent`, never `ReturnLoanCommand`. It is a fact,
  and a handler may not veto it.
- **`record`**, immutable, carrying ids and values — not entity references. The
  handler runs after the transaction; a live entity reference is a trap.
- `OccurredAt` is stamped at construction.

> `DomainEvent` initialises `OccurredAt` from `DateTimeOffset.UtcNow`, the one
> place in the solution that reads the clock directly. `IClock` lives in
> Application and Domain references nothing, so the alternatives are a domain
> dependency or passing the instant in at every call site. If this ever needs to
> be controllable in a test, pass it in — do not give Domain a reference.

---

## Recursion and tree structures

`Category` is self-referencing, and `MoveUnder` currently blocks only direct
self-parenting. A longer cycle (A → B → A) is still creatable — open issue #4.

When fixing it, **walk ancestors iteratively, not recursively**. On an
already-cyclic graph a recursive walk does not return a wrong answer, it
overflows the stack — an unrecoverable crash reachable from a user request. The
1Rivet standard's "avoid recursion, use loops" says the same thing.

The walk needs to load ancestors, so it belongs in the **service**, not in
`MoveUnder`. Keep the entity's cheap self-check where it is; it catches the
common case without a query.

---

## Checklist for a new entity

1. `sealed class`, deriving `AuditableEntity` (or `Entity` if no timestamps or
   events are needed).
2. Private parameterless constructor for EF; private full constructor.
3. `private set` on every property; `= null!` on required reference types.
4. Static `Create` factory that validates and throws
   `BusinessRuleViolationException` with a `resource.reason` code.
5. Collections: private `List<T>`, exposed as `IReadOnlyCollection<T>`.
6. Mutating methods named for the business action; each re-validates.
7. Value objects for any field with a format rule.
8. No EF attributes — mapping goes in an `IEntityTypeConfiguration<T>`.
9. Unit tests for each invariant, including the failure paths.
10. If the factory exceeds five parameters, introduce a parameter record — see
    gap #4 in `csharp-standards-1rivet`, which `Book.Create` currently trips.
