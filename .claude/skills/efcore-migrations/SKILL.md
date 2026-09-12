---
name: efcore-migrations
description: Create, review, and apply EF Core migrations in this dual-provider solution. Use whenever the domain model changes, a migration must be generated or rolled back, or the database needs resetting.
---

# EF Core migrations

This solution targets **two providers** — SQLite in development, SQL Server
later — and keeps their migration sets separate.

---

## The commands

Migrations run against `Library.Infrastructure` **alone**. Do **not** pass
`--startup-project`: `DesignTimeDbContextFactory` supplies the context, which is
what keeps `Microsoft.EntityFrameworkCore.Design` (build-time tooling) out of the
API project.

```bash
# Add
dotnet ef migrations add <Name> \
  --project src/Library.Infrastructure \
  --context LibraryDbContext \
  --output-dir Persistence/Migrations

# Review the SQL — ALWAYS, before applying
dotnet ef migrations script --project src/Library.Infrastructure --context LibraryDbContext

# Apply
dotnet ef database update --project src/Library.Infrastructure --context LibraryDbContext

# Undo the last migration (only if not yet applied anywhere shared)
dotnet ef migrations remove --project src/Library.Infrastructure --context LibraryDbContext

# List
dotnet ef migrations list --project src/Library.Infrastructure --context LibraryDbContext
```

Tooling out of date?

```bash
dotnet tool update --global dotnet-ef
```

The tool version should match the EF Core package version in
`Directory.Packages.props`.

---

## Naming

Describe the change, `PascalCase`, no date prefix — EF adds a timestamp.

Good: `InitialCatalogue`, `AddLoanAndFine`, `AddMemberUserLink`,
`AddLoanActiveCopyIndex`
Bad: `Update1`, `Fix`, `Changes`, `Migration2`

---

## Review before applying — every time

```bash
dotnet ef migrations script --project src/Library.Infrastructure --context LibraryDbContext -o /tmp/schema.sql
```

Check for:

1. **Unintended drops.** `DropColumn` / `DropTable` you did not ask for. EF
   sometimes infers a drop-and-recreate where an alter was expected — and the
   data goes with it.
2. **Renames seen as drop + add.** EF cannot detect a rename. Renaming a property
   produces `DropColumn` + `AddColumn`, silently discarding the data. Fix by
   editing the migration to use `RenameColumn`.
3. **Missing indexes.** A new foreign key without a supporting index means a scan
   on every join.
4. **Wrong delete behaviour.** Check each `ON DELETE`. `CASCADE` where `RESTRICT`
   was intended is a data-loss bug waiting for a careless click.
5. **Nullability changes.** Making an existing column `NOT NULL` fails if any row
   holds null. Add the column nullable, backfill, then tighten — in three
   migrations.

---

## Dual-provider rules

The two providers need **separate migration sets**, because the generated DDL
genuinely differs — SQLite cannot `ALTER COLUMN`, so EF rewrites whole tables for
changes SQL Server performs in place. One shared set would be wrong for one of
them.

```bash
# SQLite (default) -> Persistence/Migrations
dotnet ef migrations add <Name> --project src/Library.Infrastructure --context LibraryDbContext --output-dir Persistence/Migrations

# SQL Server -> Persistence/Migrations/SqlServer
LIBRARY_DB_PROVIDER=SqlServer dotnet ef migrations add <Name> \
  --project src/Library.Infrastructure --context LibraryDbContext \
  --output-dir Persistence/Migrations/SqlServer
```

`LIBRARY_DB_PROVIDER` is read by `DesignTimeDbContextFactory`.

**The rule that keeps this cheap:** no provider-specific SQL, types, or functions
outside `Library.Infrastructure`. The moment a raw `NVARCHAR(MAX)` or a `DATEADD`
reaches a query, the swap stops being free.

---

## Provider differences that matter here

| Concern | SQLite | SQL Server |
|---|---|---|
| Filtered/partial index | `WHERE` on `CREATE INDEX` — supported | Filtered index — supported |
| `ALTER COLUMN` | Not supported; table rewritten | Supported |
| `decimal` | No native type — **matters for `Fine.Amount`** | `decimal(18,2)` |
| `DateTimeOffset` | Stored as ISO-8601 text; sorts correctly | Native |
| Max length | Advisory only — not enforced | Enforced |

The `decimal` row is the sharp edge. Money arithmetic should be verified against
SQL Server before the Phase 4 fine logic is considered finished.

---

## The filtered index (Phase 4)

The system's headline rule is enforced by the database:

```sql
CREATE UNIQUE INDEX IX_Loan_ActiveCopy
  ON Loan(BookCopyId) WHERE ReturnedAt IS NULL;
```

In the configuration:

```csharp
builder.HasIndex(l => l.BookCopyId)
    .IsUnique()
    .HasFilter("[ReturnedAt] IS NULL")     // SQL Server bracket syntax
    .HasDatabaseName("IX_Loan_ActiveCopy");
```

> The filter string is **provider-specific syntax** — SQL Server uses
> `[ReturnedAt]`, SQLite uses `"ReturnedAt"`. This is one of the few places the
> dual-provider abstraction leaks, so set it per provider in the configuration
> and verify the generated SQL for both.

Confirm it is in the migration output. Without it the rule is enforced only by an
application check that two concurrent requests can both pass.

---

## Generated code

`Persistence/Migrations/*.cs` is generated, and excluded from analysis:

```ini
[**/Persistence/Migrations/*.cs]
generated_code = true
dotnet_analyzer_diagnostic.severity = none
```

Without it, `TreatWarningsAsErrors` fails the build on EF's own output.

**Editing a generated migration is legitimate** for a rename (`DropColumn` +
`AddColumn` → `RenameColumn`) or to add a data-backfill step. Never edit one that
has already been applied to a shared database.

---

## Resetting locally

```bash
rm src/Library.Api/library.db          # Windows: del src\Library.Api\library.db
dotnet run --project src/Library.Api   # recreated + reseeded
```

Works because `ApplyMigrationsOnStartup: true` in Development, and the seeder
no-ops once any book exists.

---

## After changing the model

1. Change the entity in `Library.Domain`.
2. Update the `IEntityTypeConfiguration<T>`.
3. `dotnet build` — the model must compile before scaffolding.
4. `dotnet ef migrations add <Name>`.
5. **Read the generated SQL.**
6. Delete `library.db` and run, or `dotnet ef database update`.
7. Verify through the API, not just the schema.
8. Commit the migration **with** the model change — never separately.

---

## Common failures

**`Your startup project doesn't reference Microsoft.EntityFrameworkCore.Design`**
You passed `--startup-project`. Don't.

**`Build failed`** after adding a migration
Usually an analyzer error in the generated file. Confirm the `.editorconfig`
generated-code section still matches the output directory.

**`MSB3021: Unable to copy file ... being used by another process`**
The API is running. Stop it:
```powershell
Get-Process dotnet | Stop-Process -Force
```

**`The model backing the context has changed`**
There are pending model changes with no migration. Add one.

**Applying to production**
Generate an idempotent script and have it reviewed:
```bash
dotnet ef migrations script --idempotent --project src/Library.Infrastructure --context LibraryDbContext -o deploy.sql
```
Never run `database update` against production from a developer machine.
