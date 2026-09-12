---
description: Drop the local SQLite database and rebuild it from migrations plus seed data.
---

Reset the local development database.

This is destructive but safe: the SQLite file is build output, not source. It is
in `.gitignore`, and migrations plus the seeder recreate it exactly.

## 1. Stop the API

It holds a lock on the file.

```powershell
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force
```

## 2. Delete the database

```bash
rm -f src/Library.Api/library.db src/Library.Api/library.db-shm src/Library.Api/library.db-wal
rm -f src/Library.Infrastructure/library-design-time.db
```

## 3. Confirm the migrations are current

```bash
dotnet build LibraryManagement.slnx -c Release
dotnet ef migrations list --project src/Library.Infrastructure --context LibraryDbContext
```

If the model has changed since the last migration, add one **before**
recreating — otherwise the fresh database will not match the model.

## 4. Recreate

```bash
dotnet run --project src/Library.Api
```

In Development, `ApplyMigrationsOnStartup: true` applies migrations and then
seeds. Expect:

```
[INF] Applying pending migrations (Sqlite)
[INF] Migrations applied
[INF] Seeding catalogue...
[INF] Seeded 15 books with 41 copies.
[INF] Now listening on: http://localhost:5112
```

## 5. Verify

```bash
curl http://localhost:5112/health/ready          # Healthy
curl "http://localhost:5112/api/books?pageSize=3"
```

Confirm the seed counts match the log. If the seeder throws, the message names
the offending record — it validates sample data through the domain factories, so
a bad ISBN fails loudly here rather than populating a broken row. (This has
happened: a check-digit typo in the seed set was caught exactly this way.)

## Notes

- The seeder is a no-op once any book exists, so it is safe on every startup.
- To inspect the result:
  ```bash
  sqlite3 src/Library.Api/library.db ".tables"
  sqlite3 src/Library.Api/library.db "SELECT Title, Isbn FROM Books LIMIT 5;"
  ```
- For SQL Server, drop the database in SSMS or Azure Data Studio and run
  `dotnet ef database update` — do **not** delete a file.
