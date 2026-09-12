---
name: dotnet-clean-architecture
description: Add a feature across the four Clean Architecture layers of this solution. Use when adding a new entity, endpoint, or use case to the Library Management API — it gives the file-by-file checklist and the layer rules that must not be broken.
---

# Adding a feature across the layers

The dependency rule in this solution is enforced by project references:

```
Library.Domain          (no references, no packages)
    ^
Library.Application     -> Domain
    ^
Library.Infrastructure  -> Application, Domain
    ^
Library.Api             -> Application, Infrastructure
```

Work **inside out**. Each step compiles before the next begins.

---

## Step 1 — Domain

`src/Library.Domain/Entities/<Name>.cs`

```csharp
public sealed class Thing : AuditableEntity
{
    private Thing() { }                              // EF Core only

    private Thing(string name) => Name = name;

    public string Name { get; private set; } = null!;

    private readonly List<Child> _children = [];
    public IReadOnlyCollection<Child> Children => _children.AsReadOnly();

    public static Thing Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleViolationException("thing.name_required", "Name is required.");

        return new Thing(name.Trim());
    }

    public void Rename(string name) { /* validate, then assign */ }
}
```

Rules:
- `sealed`, private constructor, `private set` on every property.
- Static factory that validates. Never a public constructor.
- Collections: private `List<T>`, exposed as `IReadOnlyCollection<T>`.
- State changes go through named methods, not setters.
- **No EF Core attributes.** No `using Microsoft.*` of any kind.
- Throw `BusinessRuleViolationException` (422) or `ConflictException` (409) with
  a stable snake-case error code.

Enums go in `Domain/Enums/` with **explicit numeric values** — they are
persisted, and letting the compiler assign them means inserting a member
renumbers every value after it.

---

## Step 2 — Application: DTOs

`src/Library.Application/<Feature>/Dtos/<Name>Dtos.cs`

`record` types, `init` accessors. Usually two: a lean `<Name>SummaryDto` for
lists and a fuller `<Name>DetailDto`.

Never return an entity from an endpoint.

---

## Step 3 — Application: abstractions

`src/Library.Application/<Feature>/I<Name>Repository.cs`

```csharp
public interface IThingRepository
{
    Task<PagedResult<ThingSummaryDto>> SearchAsync(ThingSearchRequest r, CancellationToken ct = default);
    Task<ThingDetailDto?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<bool> ExistsAsync(int id, CancellationToken ct = default);
}
```

- Return **materialised** DTOs or `PagedResult<T>`. Never `IQueryable`.
- Return `null` for a miss — the service decides what that means.
- `CancellationToken` last, always.

---

## Step 4 — Application: service

`src/Library.Application/<Feature>/<Name>Service.cs`

The service translates "not in the database" into "404 for the caller":

```csharp
ThingDetailDto? thing = await _repository.GetByIdAsync(id, ct);
return thing ?? throw new NotFoundException("Thing", id);
```

An **empty search result is success**, not a 404. Only a request for a specific
resource can be not-found.

Register in `Application/DependencyInjection.cs` as **scoped**.

---

## Step 5 — Infrastructure: EF configuration

`src/Library.Infrastructure/Persistence/Configurations/<Name>Configuration.cs`

```csharp
public sealed class ThingConfiguration : IEntityTypeConfiguration<Thing>
{
    public void Configure(EntityTypeBuilder<Thing> builder)
    {
        builder.ToTable("Things");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.HasIndex(t => t.Name).IsUnique().HasDatabaseName("IX_Things_Name");

        builder.HasOne(t => t.Parent).WithMany(p => p.Things)
            .HasForeignKey(t => t.ParentId)
            .OnDelete(DeleteBehavior.Restrict);      // choose per relationship

        builder.Metadata.FindNavigation(nameof(Thing.Children))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(t => t.SomeComputedProperty);
    }
}
```

Picked up automatically by `ApplyConfigurationsFromAssembly`. Add a `DbSet` to
`LibraryDbContext` only if the entity is queried directly.

**Delete behaviour is a decision, not a default:**
- `Restrict` — the parent is a classification whose deletion must not remove children
- `Cascade` — the child is meaningless without the parent
- `SetNull` — the relationship is optional

**Never `HasConversion` on a type you need to search *within*.** The converter is
applied to both sides of a comparison, so `LIKE` breaks. Use a private string
backing field instead — see `Book.Isbn`.

---

## Step 6 — Infrastructure: repository

`src/Library.Infrastructure/Repositories/<Name>Repository.cs`

For a listing query, in this order:

```csharp
IQueryable<Thing> query = _context.Things.AsNoTracking();   // 1. reads don't track

query = ApplyFilters(query, request);                        // 2. conditional Where
int totalCount = await query.CountAsync(ct);                 // 3. count before paging
if (totalCount == 0) return PagedResult.Empty<...>(...);

query = ApplySorting(query, request);                        // 4. whitelist + ThenBy(Id)

var items = await query.Skip(...).Take(...)
    .Select(t => new ThingSummaryDto { ... })                // 5. project IN the query
    .ToListAsync(ct);
```

- Each filter inside `if (supplied)`, so an absent filter contributes no SQL.
- `Select` **before** `ToListAsync` — this is what prevents N+1 and over-fetching.
- Aggregate counts in SQL (`.Count(c => ...)`), never over a loaded collection.
- Always `ThenBy(x => x.Id)` — SQL gives no ordering among ties, so paging is
  otherwise unstable.

Register in `Infrastructure/DependencyInjection.cs` as **scoped**.

---

## Step 7 — Migration

```bash
dotnet ef migrations add <Name> --project src/Library.Infrastructure --context LibraryDbContext --output-dir Persistence/Migrations
dotnet ef migrations script --project src/Library.Infrastructure --context LibraryDbContext
```

**Read the generated SQL before applying it.** EF occasionally infers a
drop-and-recreate where an alter was expected.

---

## Step 8 — API controller

`src/Library.Api/Controllers/<Name>Controller.cs`

```csharp
[ApiController]
[Route("api/things")]
[Produces("application/json")]
public sealed class ThingsController : ControllerBase
{
    [HttpGet("{id:int}", Name = "GetThingById")]
    [ProducesResponseType<ThingDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ThingDetailDto>> GetById(int id, CancellationToken ct)
    {
        return Ok(await _thingService.GetByIdAsync(id, ct));
    }
}
```

- **No `try`/`catch`.** `GlobalExceptionHandler` maps exceptions centrally.
- No business logic, no EF Core.
- `[ProducesResponseType]` on every outcome — it is what OpenAPI documents.
- XML doc comments become the Swagger description; write them for the caller.

---

## Step 9 — Tests, docs, tracker

- Unit tests for domain rules and service behaviour (mock the repository).
- Integration tests through `LibraryApiFactory` for the HTTP contract.
- Update `docs/api-contract.md` and the relevant `docs/phases/phase-NN.md`.
- Tick the item in `TASKS.md`, and add a row to the Decisions Log for any
  non-obvious choice.

---

## Final check

```bash
dotnet build LibraryManagement.slnx -c Release    # must be 0 warnings
```

Then actually call the endpoint and read the response. Compiling is not verifying.
