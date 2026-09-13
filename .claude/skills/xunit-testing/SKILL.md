---
name: xunit-testing
description: How tests are written and run in this solution — xUnit v3 + NSubstitute + Shouldly, the unit/integration split, FakeClock, LibraryApiFactory, and the .NET 10 test-runner workaround. Use when writing, fixing, or running any test here.
---

# Testing

xUnit **v3**, NSubstitute, Shouldly. Two test projects with a hard boundary
between them.

---

## Running tests — read this first

```powershell
dotnet build LibraryManagement.slnx -c Release
dotnet test --solution LibraryManagement.slnx -c Release
```

**Note `--solution`.** Passing the solution positionally —
`dotnet test LibraryManagement.slnx` — fails with *"Specifying a solution for
'dotnet test' should be via '--solution'"*. The flag is required under the new
runner.

This works because of `global.json` at the repository root:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

The .NET 10 SDK retired the VSTest bridge, and xUnit v3 targets
Microsoft.Testing.Platform (MTP) instead. `global.json` is what selects MTP;
neither a `dotnet.config` `[dotnet.test.runner]` section nor the
`TestingPlatformDotnetTestSupport` MSBuild property does it. If `dotnet test`
ever starts failing with a VSTest error, that file is the first thing to check.

Under MTP each test project also **builds as an executable that hosts its own
runner**, so a single project can still be run directly — useful for a fast loop
on one suite:

```powershell
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe
```

Useful MTP flags: `--filter-class`, `--filter-method`, `--list-tests`.

This is also why neither test project references `Microsoft.NET.Test.Sdk` or
`xunit.runner.visualstudio`: both belong to the VSTest world. Adding them back
will not fix the runner, it will break the build.

---

## The two projects

| | `Library.UnitTests` | `Library.IntegrationTests` |
|---|---|---|
| References | Domain, Application | The API host |
| Database | None | Real SQLite, one file per factory |
| HTTP | None | In-memory transport |
| Speed | Milliseconds | Hundreds of ms |
| Answers | "Is this logic right?" | "Is it wired up right?" |

The project reference list is the enforcement. `Library.UnitTests` cannot reach
Infrastructure, so a test that needs a database *cannot* be written there — it
will not compile. If you find yourself wanting one, the test belongs in
`Library.IntegrationTests`.

---

## Naming

Test method names are sentences, with underscores:

```csharp
public void Page_below_one_is_clamped_to_the_first_page(int page)
public async Task An_unmatched_route_returns_ProblemDetails_not_an_empty_body()
```

CA1707 (no underscores in member names) is disabled **for test projects only**.
The reason is the runner output: a failure line reading
`Skip_is_derived_from_page_and_size` states the broken behaviour without anyone
opening the file. `SkipIsDerivedFromPageAndSize` does not read as well, and
`Test3` tells you nothing at all.

Name the *behaviour and its expected outcome*, never the method under test.

---

## Assertions — Shouldly

`using Shouldly` is a global `Using` in both csproj files; do not import it
per-file.

```csharp
result.Page.ShouldBe(1);
response.StatusCode.ShouldBe(HttpStatusCode.OK);
copies.ShouldNotBeEmpty();
act.ShouldThrow<ConflictException>().ErrorCode.ShouldBe("copy.duplicate_barcode");
```

Shouldly over `Assert.Equal` because the failure message names the expression:

```
result.Page
    should be
1
    but was
0
```

`Assert.Equal(1, result.Page)` reports only "Expected 1, Actual 0" and leaves you
to find which of four assertions in the method fired.

Assert on the **error code**, not the message. Messages are prose and get
reworded; `ErrorCode` is the contract.

---

## Test doubles — NSubstitute

```csharp
IBookRepository repository = Substitute.For<IBookRepository>();
repository.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((BookDetailDto?)null);

BookService service = new(repository, clock);

await Should.ThrowAsync<NotFoundException>(
    () => service.GetByIdAsync(42, TestContext.Current.CancellationToken));
```

- Substitute for **interfaces we own** — `IBookRepository`, `IClock`,
  `IUnitOfWork`. Not for `DbContext`, not for types from the framework.
- `Arg.Any<CancellationToken>()` on every async call, or the stub silently fails
  to match and returns `null`.
- Verify an interaction only when the interaction *is* the behaviour
  (`await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>())`).
  Otherwise assert on the result — verifying every call makes the test a
  restatement of the implementation, and it then fails on every harmless
  refactor.

**Never substitute `IClock`.** Use `FakeClock` — it is real, controllable, and
reads better than three `Returns` stubs.

---

## Time — `FakeClock`

`tests/Library.UnitTests/Common/FakeClock.cs`. This is the payoff for the
no-`DateTime.Now` rule:

```csharp
FakeClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
Loan loan = Loan.Issue(copy, member, clock.UtcNow, loanPeriodDays: 14);

clock.AdvanceDays(26);              // 12 days past due

loan.IsOverdue(clock.UtcNow).ShouldBeTrue();
loan.CalculateFine(clock.UtcNow, ratePerDay: 50m).ShouldBe(600m);
```

`Advance`, `AdvanceDays`, `SetTo`, and `FakeClock.DefaultNow` for tests that do
not care about the date.

Without this, the same test needs either a 12-day wait or a back-dated loan that
quietly assumes the arithmetic is symmetric — and it is that assumption, not the
wait, that hides real bugs.

---

## Cancellation tokens in tests

xUnit v3 gives every test a token. Pass it to every async call:

```csharp
await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
```

Omitting it means a hung test blocks the whole run instead of failing its own
timeout. This is a v3 feature; v2 examples found online will not have it.

---

## Integration tests — `LibraryApiFactory`

Boots the real `Program.cs` — same DI container, same middleware order — over an
in-memory transport. No port, no server process.

```csharp
public sealed class BookEndpointTests : IClassFixture<LibraryApiFactory>
{
    private readonly LibraryApiFactory _factory;

    public BookEndpointTests(LibraryApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Creating_a_book_with_a_duplicate_ISBN_returns_409()
    {
        HttpClient client = _factory.CreateClient();
        // ...
    }
}
```

Three decisions baked into that factory, worth knowing before you change it:

**SQLite, not EF Core's InMemory provider.** InMemory is not a relational
database: it ignores unique indexes, foreign keys, and check constraints. A suite
built on it would happily allow two active loans on the same copy — the one rule
this system exists to enforce. SQLite honours them, so the tests test them.

**A private database file per factory instance**, named with a GUID, deleted on
disposal. Classes cannot see each other's rows, so xUnit can run them in
parallel.

**Configuration overridden through `UseSetting`**, which writes into
configuration *before* the host reads it. No need to remove and re-register the
`DbContext` afterwards.

> `InitializeAsync` currently calls `EnsureCreatedAsync`, which builds the schema
> from the model and skips migration history. That was correct in Phase 1. Now
> that migrations exist it should be `MigrateAsync()`, so the tests exercise the
> same migrations that ship. See the `efcore-migrations` skill.

To swap a real collaborator for a fake — binding `IClock` to a `FakeClock` so a
test can fast-forward past a due date — use the `ConfigureServices` block that is
already stubbed out in `ConfigureWebHost`.

---

## What to test at each level

**Unit — the logic that is easy to get wrong and cheap to check.**

- Entity factories and invariants: `Book.Create` rejecting a blank title.
- Value objects: `Isbn` check digits, hyphen normalisation, the `TryCreate`
  failure paths.
- Anything arithmetic or date-based: overdue calculation, fine accrual, paging
  arithmetic.
- Boundary clamps. `PageRequestTests` is the model here — the `pageSize=1000000`
  case is the point of the class: without a server-side ceiling, one
  unauthenticated request materialises a million rows and becomes a denial of
  service.

**Integration — everything between HTTP and the logic.**

- Status codes, especially the 404 / 409 / 422 distinctions.
- The ProblemDetails shape and `errorCode` on failures.
- Routing, model binding, JSON casing, strict-JSON rejection of unknown fields.
- Anything enforced by a database constraint. The Phase 4 filtered unique index
  on `Loan(BookCopyId) WHERE ReturnedAt IS NULL` is only testable here, and the
  concurrent-issue race is the test that matters most in the suite.

Do not test: EF Core, ASP.NET Core, or FluentValidation themselves. Test that
*our* rules hold.

---

## Structure

Arrange / Act / Assert, separated by blank lines, no comment labels. One
behaviour per test. `[Theory]` with `[InlineData]` when the same behaviour has
several inputs — three clamping inputs are one test, not three.

A comment in a test earns its place only by explaining *why the case matters*:

```csharp
// Page 4 at 20 per page skips the first 60 rows, not 80 - Page is 1-based.
```

---

## Before ticking a test item in TASKS.md

1. Both executables run and all tests pass — paste the counts.
2. The new test fails when the behaviour is broken. A test that passes against
   both the fixed and the broken code is testing nothing; confirm it by breaking
   the code once.
3. `dotnet build -c Release` is still at **0 warnings**.
