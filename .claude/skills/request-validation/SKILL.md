---
name: request-validation
description: Where a rule belongs — FluentValidation validator, entity factory, service, or database constraint — plus the ValidationFilter pipeline, the deprecated-package trap, and the 400/422/409 boundary. Use when adding a write endpoint, a request DTO, or any new business rule.
---

# Request validation

FluentValidation **12**, core package plus DI extensions, invoked explicitly by
a filter.

---

## The package trap

**Never add `FluentValidation.AspNetCore`.** It is deprecated. The author removed
its MVC auto-validation because running validators inside model binding made
failures hard to trace and prevented async rules. Most tutorials online still
show it.

What we reference (`Directory.Packages.props`):

```xml
<PackageVersion Include="FluentValidation" Version="12.1.1" />
<PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />
```

The supported path is explicit invocation — which is what `ValidationFilter`
does at one well-defined point in the pipeline.

---

## Four places a rule can live. Pick deliberately.

| Layer | Answers | Can reach the DB? | Failure |
|---|---|---|---|
| **Validator** | "Is this request well-formed?" | No | 422 with per-field `errors` |
| **Entity factory / method** | "Would this break an invariant?" | No | `BusinessRuleViolationException` → 422 |
| **Service** | "Does this collide with existing state?" | Yes | `NotFoundException` → 404, `ConflictException` → 409 |
| **Database constraint** | "Did two requests race?" | It *is* the DB | `DbUpdateException` → translated to 409 |

### Validator — shape only

Required, length, range, format, enum membership, cross-field consistency within
the one request. Everything it needs is in the payload.

```csharp
RuleFor(r => r.Title)
    .NotEmpty().WithMessage("Title is required.")
    .MaximumLength(500).WithMessage("Title must be 500 characters or fewer.");

RuleFor(r => r.Condition).IsInEnum().WithMessage("Unknown condition value.");
```

`.IsInEnum()` matters more than it looks: an enum parameter can legally hold
`(CopyCondition)99`, and without this it reaches the database as an integer no
one can interpret.

**Delegate to the domain rather than restate it:**

```csharp
private static bool BeAValidIsbn(string isbn) => Isbn.TryCreate(isbn, out _, out _);
```

`Isbn` already encodes the format and check-digit rules. A second copy here would
be free to drift. The validator produces the friendly per-field message; the
value object stays the authority.

**A validator must not query the database.** Two reasons, and the second is the
serious one: it adds a round trip to every request, and it creates a
check-then-act race. Between "no book has this ISBN" and the insert, another
request can insert it. Uniqueness belongs to the service *and* a unique index —
never to a validator.

### Entity factory — invariants

Rules that must hold for the object to exist at all, restated in the domain
because the domain cannot trust that a validator ran:

```csharp
if (string.IsNullOrWhiteSpace(title))
{
    throw new BusinessRuleViolationException("book.title_required", "Book title is required.");
}
```

Yes, this duplicates the validator's `NotEmpty`. That is intentional. The
validator gives the API client a good error message; the factory guarantees the
invariant for every other caller — the seeder, an import job, a future background
service — none of which pass through MVC.

### Service — state

Existence checks, uniqueness, and anything that depends on what is already in the
database. This is where 404 and 409 come from.

### Database — the race

Unique indexes and filtered unique indexes are the only thing that actually holds
under concurrency. The Phase 4 index on `Loan(BookCopyId) WHERE ReturnedAt IS NULL`
is the canonical case: the service check narrows the window, the index closes it.
`UnitOfWork` translates the resulting `DbUpdateException` into a 409.

---

## The pipeline — `ValidationFilter`

Registered globally, so an unvalidated request is impossible rather than merely
unlikely. For each action argument it resolves `IValidator<T>` for the argument's
runtime type, runs it, collects failures across **all** arguments, and throws one
`ValidationException`.

Three properties worth preserving if you touch it:

1. **It throws rather than returning a result.** The throw routes through
   `GlobalExceptionHandler`, which already renders 422 with a per-field `errors`
   object. Returning `BadRequestObjectResult` here would create a second error
   shape that merely resembles the first, and clients would have to parse both.
2. **Arguments with no validator are skipped**, so an `int id` or a
   `CancellationToken` costs nothing.
3. **Validators resolve per request from DI**, so a validator may depend on
   scoped services — which is how a validator gets an `IClock`.

Registration is by assembly scan in `Library.Application/DependencyInjection.cs`.
A new validator needs no registration line; it needs to be `public` and to derive
from `AbstractValidator<T>`.

---

## Status codes

| Code | Source |
|---|---|
| **400** | Model binding failed before validation ran — malformed JSON, `?page=abc`, or an unknown property (strict JSON is on) |
| **422** | Validator failed, or an entity invariant was violated. The request is understood and wrong |
| **404** | The service looked and it is not there |
| **409** | Valid, but it collides with current state |

The test that settles 422 vs 409:

> Could this request succeed later, unchanged?
> **Yes → 409** ("that copy is on loan"). **No → 422** ("title is required").

Full policy and the ProblemDetails shape: `api-contract-docs`.

---

## Adding a validator

1. Add the rule to the existing `*RequestValidators.cs` for that feature, or a
   new file if it is a new feature.
2. `public sealed class XRequestValidator : AbstractValidator<XRequest>`.
3. Message on **every** rule. The default ("'Page Count' must be greater than
   '0'.") leaks the property name as the client never spelled it.
4. `.When(...)` on optional properties, or a null `PageCount` fails
   `GreaterThan(0)`.
5. Delegate format rules to the domain type if one exists.
6. Add a unit test with a valid request and one per failure path.

---

## Conventions that are easy to miss

**Messages end in a full stop and name the field in client terms.** They are
shown to a human; the machine-readable half of the contract is the field name in
`errors`.

**Prefer duplication over a shared base validator.** `AddBookCopyRequestValidator`
and `UpdateBookCopyRequestValidator` repeat three barcode rules on purpose: the
two requests are free to diverge, and a shared base would make any divergence a
breaking change for both. Three lines of duplication, no coupling.

**Keep limits in step with the database.** `MaximumLength(500)` must match
`HasMaxLength(500)` in the EF configuration. Today they are separate literals in
two files with nothing enforcing agreement — see gap #5 in
`csharp-standards-1rivet`. If you change one, change both.

**Do not read the clock in a validator.** The three "date not in the future"
rules currently call `DateTime.UtcNow`, which violates project rule 6 and makes
them untestable — gap #1 in `csharp-standards-1rivet`. New date rules should take
`IClock` through the constructor:

```csharp
public sealed class CreateBookRequestValidator : AbstractValidator<CreateBookRequest>
{
    public CreateBookRequestValidator(IClock clock)
    {
        RuleFor(r => r.PublishedOn)
            .LessThanOrEqualTo(_ => clock.Today.AddDays(1))
            .When(r => r.PublishedOn.HasValue)
            .WithMessage("Publication date cannot be in the future.");
    }
}
```

The filter resolves validators from DI per request, so the constructor dependency
works with no other change.

---

## Request DTOs

Validation and the request type are one design. Both exist to make illegal input
unrepresentable.

- **Never bind to an entity.** A dedicated request record lists exactly what a
  client may supply; binding to `Book` would let a caller set `Id` or `CreatedAt`
  (OWASP API6, mass assignment). `private set` on entity properties is the second
  layer of that defence.
- **Leave out what a client must not control.** `UpdateBookRequest` has no
  `Isbn` — it is the natural key, so changing it would make this a different
  book. `UpdateBookCopyRequest` has no `Status` — a copy becomes `OnLoan` by
  being issued, and letting a client set it directly would desynchronise it from
  the loan rows that are the source of truth.
- **`PUT` replaces, it does not merge.** `AuthorIds` replaces the whole list,
  which is what keeps `PUT` idempotent and spares the caller a diff.
- **Unknown properties are rejected**, not ignored — a typo'd field name is a
  400, not a silently dropped value.
