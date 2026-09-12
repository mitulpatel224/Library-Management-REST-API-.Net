---
description: Add one endpoint across all four layers, following the solution's conventions.
argument-hint: "[METHOD /route — e.g. POST /api/books]"
---

Add the endpoint **$ARGUMENTS** to the Library Management API.

Load the `dotnet-clean-architecture` skill for the file-by-file checklist and the
`api-contract-docs` skill for the status-code and ProblemDetails rules.

## Before writing anything

1. Read the existing controller and service for this resource. Match their shape
   — do not invent a second style alongside the first.
2. Check `docs/api-contract.md` to see whether this endpoint is already
   specified. If it is, build what is written there.
3. Decide and state the status codes: success, not-found, conflict, validation.

## Build inward out

Work through the layers in order, compiling at each step:

**Domain** — any new rule goes on the entity as a method, throwing
`BusinessRuleViolationException` (422) or `ConflictException` (409) with a stable
snake-case error code.

**Application** —
- request DTO as a `record` (a *dedicated* one; never bind to an entity, which is
  how mass-assignment bugs happen);
- a FluentValidation validator beside it, named `<Request>Validator`;
- the repository interface method, returning materialised DTOs — never
  `IQueryable`;
- the service method, translating a miss into `NotFoundException`.

**Infrastructure** — the repository implementation. For writes: load, mutate
through entity methods, `SaveChangesAsync`. Translate a unique-index violation
(`DbUpdateException`) into `ConflictException` — the pre-check is for the message,
the index is the guarantee.

**API** — the controller action. `[ProducesResponseType]` for every outcome, XML
comments written for the caller, no `try`/`catch`, no business logic.

## Migration

If the model changed:

```bash
dotnet ef migrations add <Name> --project src/Library.Infrastructure --context LibraryDbContext --output-dir Persistence/Migrations
dotnet ef migrations script --project src/Library.Infrastructure --context LibraryDbContext
```

**Read the generated SQL before applying it.**

## Verify

```bash
dotnet build LibraryManagement.slnx -c Release   # 0 warnings
dotnet run --project src/Library.Api
```

Call it. Every path:

- happy path returns the documented shape and status;
- not-found → 404 with the right `errorCode`;
- invalid input → 422 with a per-field `errors` object;
- conflict → 409;
- for a `201`, the `Location` header resolves.

Watch the logged SQL for N+1.

## Finish

- Tests: unit for the rule, integration for the HTTP contract.
- `docs/api-contract.md` — with a **real** example response, copied from an actual
  call rather than invented.
- `TASKS.md` — tick the item.

Then report what works and how the user can verify it themselves.
