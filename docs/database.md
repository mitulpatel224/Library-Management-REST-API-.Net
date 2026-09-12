# Database Configuration

Provider selection, connection strings, migrations, and the SQL Server switch.

---

## Why two providers

The development machine had no database engine installed, and an assessment
reviewer's machine probably does not either. SQLite makes the repository
clone-and-run: no install, no service, no credentials.

SQL Server is nonetheless the conventional expectation for a .NET project, so the
solution is built to move to it as a **configuration change**, not a rewrite.

That only stays true because of one discipline enforced everywhere else:

> **No provider-specific SQL, types, or functions escape `Library.Infrastructure`.**

The moment a raw `NVARCHAR(MAX)`, a `DATEADD`, or a `FromSqlRaw` with T-SQL
appears in a query, the swap stops being free.

---

## Provider selection

One method in `Infrastructure/DependencyInjection.cs` decides:

```csharp
switch (options.Provider)
{
    case DatabaseProviders.SqlServer:
        builder.UseSqlServer(options.ConnectionString, sql =>
        {
            sql.MigrationsAssembly("Library.Migrations.SqlServer");
            sql.CommandTimeout(options.CommandTimeoutSeconds);
            sql.EnableRetryOnFailure(maxRetryCount: 3, ...);
        });
        break;

    case DatabaseProviders.Sqlite:
        builder.UseSqlite(options.ConnectionString, sqlite =>
        {
            sqlite.MigrationsAssembly("Library.Infrastructure");
            sqlite.CommandTimeout(options.CommandTimeoutSeconds);
        });
        break;

    default:
        throw new InvalidOperationException($"Unsupported provider '{options.Provider}'.");
}
```

The `default` arm throwing is deliberate: a typo in configuration fails the
process at startup with a clear message, rather than silently falling back to a
provider nobody intended.

`EnableRetryOnFailure` is SQL Server only, because the faults it retries —
deadlock, timeout, Azure SQL failover — are network-database faults that a local
SQLite file cannot experience.

---

## Options

| Key | Type | Default | Purpose |
|---|---|---|---|
| `Database:Provider` | string | `Sqlite` | `Sqlite` or `SqlServer` |
| `Database:ConnectionString` | string | *(required)* | For the selected provider |
| `Database:EnableSensitiveDataLogging` | bool | `false` | Logs SQL **parameter values** |
| `Database:ApplyMigrationsOnStartup` | bool | `false` | Migrate + seed at boot |
| `Database:CommandTimeoutSeconds` | int | `30` | Guards against a runaway query |

Bound with validation at startup:

```csharp
services.AddOptions<DatabaseOptions>()
    .Bind(configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

### The two flags that must stay off in production

**`EnableSensitiveDataLogging`** writes parameter *values* into the log. From
Phase 5 that includes password hashes, email addresses, and member details —
so switching it on in production writes credentials to disk.

**`ApplyMigrationsOnStartup`** grants the running application schema-modification
rights it otherwise does not need, and makes deployment non-atomic: two instances
starting simultaneously race each other to migrate. Real deployments run
`dotnet ef database update` as a separate, reviewable step.

---

## SQLite (development default)

```json
"Database": {
  "Provider": "Sqlite",
  "ConnectionString": "Data Source=library.db"
}
```

The file is created beside `Library.Api` on first run. `*.db` is in
`.gitignore` — it is build output, not source: committing one would give every
clone one developer's local data, and a binary file conflicts on every merge.

**Reset:**

```bash
rm src/Library.Api/library.db
dotnet run --project src/Library.Api      # recreated and reseeded
```

**Inspect** with [DB Browser for SQLite](https://sqlitebrowser.org/) or the CLI:

```bash
sqlite3 src/Library.Api/library.db ".tables"
sqlite3 src/Library.Api/library.db "SELECT Title, Isbn FROM Books LIMIT 5;"
```

### Limitations worth knowing

| Limitation | Consequence here |
|---|---|
| No `ALTER COLUMN` | EF rewrites the whole table for a column change. Fine at this size |
| Dynamic typing | `NVARCHAR(500)` is advisory; SQLite will not reject a longer string |
| No native `decimal` | Stored as `TEXT`/`REAL`. **Matters from Phase 4** — fines are money |
| `DateTimeOffset` as text | Sorts correctly because it is stored ISO-8601 |
| Single writer | Irrelevant for development; would not survive production load |

The `decimal` one is the genuinely sharp edge. `Fine.Amount` must be
`decimal(18,2)` on SQL Server; SQLite will store it without complaint but
without exact decimal semantics either. Money arithmetic should be verified
against SQL Server before the fine logic is considered finished.

---

## SQL Server

### Install

1. Download [SQL Server 2022 Express](https://www.microsoft.com/sql-server/sql-server-downloads)
   (~250 MB) and choose the **Basic** installation — it creates the LocalDB
   instance automatically.
2. Optionally install [Azure Data Studio](https://azure.microsoft.com/products/data-studio/)
   or SSMS to browse.
3. Verify:
   ```powershell
   sqllocaldb info
   sqllocaldb start MSSQLLocalDB
   ```

### Configure

`appsettings.SqlServer.json` is already in the repository:

```json
{
  "Database": {
    "Provider": "SqlServer",
    "ConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=LibraryDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True",
    "EnableSensitiveDataLogging": false,
    "ApplyMigrationsOnStartup": false,
    "CommandTimeoutSeconds": 30
  }
}
```

Activate it:

```bash
dotnet run --project src/Library.Api --environment SqlServer
```

**On `TrustServerCertificate=True`:** acceptable for LocalDB, where the
connection never leaves the machine. In production it disables certificate
validation and therefore the protection against a man-in-the-middle — use a
properly issued certificate there instead.

### Generate the SQL Server migration set

The two providers need **separate** migrations, because the generated DDL
genuinely differs — SQLite cannot `ALTER COLUMN`, so EF rewrites tables for
changes SQL Server performs in place. One shared set would produce SQL that is
wrong for one of the two.

```bash
LIBRARY_DB_PROVIDER=SqlServer dotnet ef migrations add InitialCatalogue \
  --project src/Library.Infrastructure \
  --context LibraryDbContext \
  --output-dir Persistence/Migrations/SqlServer
```

`LIBRARY_DB_PROVIDER` is read by `DesignTimeDbContextFactory` and selects the
dialect to scaffold against.

> **Not yet done.** Only the SQLite migration set exists — issue 2 in
> [`../TASKS.md`](../TASKS.md). It needs SQL Server installed first.

### Connection string forms

```
# LocalDB (development)
Server=(localdb)\MSSQLLocalDB;Database=LibraryDb;Trusted_Connection=True;TrustServerCertificate=True

# Named local instance
Server=localhost\SQLEXPRESS;Database=LibraryDb;Trusted_Connection=True;TrustServerCertificate=True

# SQL authentication - NEVER commit the password; use User Secrets or env vars
Server=localhost;Database=LibraryDb;User Id=library_app;Password=<from secrets>;Encrypt=True

# Azure SQL
Server=tcp:<server>.database.windows.net,1433;Database=LibraryDb;Authentication=Active Directory Default;Encrypt=True
```

---

## Migrations

Run against `Library.Infrastructure` alone. No startup project is needed, because
`DesignTimeDbContextFactory` constructs the context — which is what keeps
`Microsoft.EntityFrameworkCore.Design` (build-time tooling) out of the API
project.

```bash
dotnet ef migrations add <Name>   --project src/Library.Infrastructure --context LibraryDbContext --output-dir Persistence/Migrations
dotnet ef migrations script       --project src/Library.Infrastructure --context LibraryDbContext
dotnet ef database   update       --project src/Library.Infrastructure --context LibraryDbContext
dotnet ef migrations remove       --project src/Library.Infrastructure --context LibraryDbContext
dotnet ef migrations list         --project src/Library.Infrastructure --context LibraryDbContext
```

### Review before applying

```bash
dotnet ef migrations script --project src/Library.Infrastructure --context LibraryDbContext -o schema.sql
```

Read it. EF occasionally infers a destructive operation — most often a
drop-and-recreate where an alter was expected — and the migration file is the
last point at which that is cheap to catch.

For deployment, generate an idempotent script that is safe to run repeatedly:

```bash
dotnet ef migrations script --idempotent --project src/Library.Infrastructure --context LibraryDbContext -o deploy.sql
```

### Generated code

`Persistence/Migrations/*.cs` is generated, and `.editorconfig` marks it so:

```ini
[**/Persistence/Migrations/*.cs]
generated_code = true
dotnet_analyzer_diagnostic.severity = none
```

Without this, warnings-as-errors fails the build on EF's own output — leaving a
choice between editing files that will be regenerated and disabling the rule
solution-wide. Scoping it to the folder keeps the strict bar on every
hand-written file.

`.gitattributes` also marks them `linguist-generated`, so a regenerated migration
is collapsed in GitHub diffs rather than drowning a pull request.

---

## Current schema

8 tables, 14 indexes. Full column dictionary in [`data-model.md`](data-model.md).

```
Categories      Publishers      Authors      Genres
Books           BookAuthors     BookGenres   BookCopies
__EFMigrationsHistory
```

Phase 4 adds `Loans` and `Fines`, including the invariant the whole system turns
on:

```sql
CREATE UNIQUE INDEX IX_Loan_ActiveCopy
  ON Loan(BookCopyId) WHERE ReturnedAt IS NULL;
```

Supported by both providers — a *partial* index in SQLite, a *filtered* index in
SQL Server. It is the enforcement of "a copy already on loan cannot be issued";
the application-level check exists only to produce a better error message.

---

## Seed data

15 books, 41 copies, plus categories, publishers, authors and genres. Created by
`DatabaseSeeder` through the domain factory methods rather than EF's `HasData`.

`HasData` bakes rows into the migration itself, so editing sample data becomes a
schema migration and every row needs a hard-coded key. Going through
`Book.Create` and `Isbn.Create` also runs the sample data through the same
validation as real input — which caught a genuine ISBN typo in this project's
own seed set on the first run.

The seeder is a no-op once any book exists, so it is safe on every startup.

---

## Production checklist

- [ ] `ApplyMigrationsOnStartup: false`; migrations run as a deployment step
- [ ] `EnableSensitiveDataLogging: false`
- [ ] Connection string from environment variables or a secret store, never
      `appsettings.json`
- [ ] `Encrypt=True` and a valid certificate; not `TrustServerCertificate`
- [ ] Application SQL login granted `db_datareader` + `db_datawriter` only —
      **not** `db_owner`; it has no reason to alter schema at runtime
- [ ] Backups configured and a restore actually tested
- [ ] `CommandTimeoutSeconds` tuned against real data volumes
