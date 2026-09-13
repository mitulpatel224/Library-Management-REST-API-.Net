---
name: csharp-standards-1rivet
description: The 1Rivet C# Coding Standards (TEC-STD-005 v1.0) mapped onto this .NET 10 solution — which rules are already enforced, which are adapted, which are deliberately deviated from and why, and which are obsolete. Use when reviewing code for standards compliance, when a reviewer cites the standard, or when justifying a deviation.
---

# 1Rivet C# Coding Standards — applied here

TEC-STD-005 v1.0, dated 22 November 2019. It predates .NET Core's maturity and
targets the .NET Framework CTS, so some rules land exactly, some need
translating, and a few are now actively wrong.

This skill is the **mapping**. For how we write C# day to day, see
`csharp-conventions` — that one is the working style guide; this one is the
compliance and justification record.

---

## The four verdicts

Every rule in the standard gets one. When a reviewer cites the standard, find
the rule below and answer with its verdict and reason — do not re-argue it.

| Verdict | Meaning |
|---|---|
| **Enforced** | We follow it. Often the compiler or an analyzer enforces it for us |
| **Adapted** | We honour the *intent* using a newer language feature the 2019 doc could not name |
| **Deviated** | We knowingly do something else. A reason is recorded, here and in the TASKS.md Decisions Log |
| **Obsolete** | The rule no longer applies on .NET 10, or following it would break the build |

A deviation that is not written down is not a deviation, it is a defect. If you
need a new one, add it here **and** to the Decisions Log, with the rejected
alternative.

---

## Naming (§4.1, §5) — Enforced almost entirely

The standard's naming table and our `.editorconfig` agree on nearly every row.

| Identifier | Standard | Here |
|---|---|---|
| Types, methods, properties, enums | PascalCase | Enforced |
| Parameters, locals | camelCase | Enforced |
| Private field | `_camelCase` | Enforced (`_isbn`, `_copies`, `_logger`) |
| Interface | `I` prefix | Enforced (`IClock`, `IBookRepository`) |
| Hungarian notation | Forbidden | Enforced |
| Boolean prefix `Is`/`Has`/`Can` | Required | Enforced (`IsTransient`, `IsValid`) |
| No parent name in property | `Customer.Name`, not `CustomerName` | Enforced (`Book.Title`) |
| Project = assembly = root namespace | Required | Enforced (`Library.Domain`) |

Three naming rows differ, all because `.editorconfig` follows the current Roslyn
defaults rather than the 2019 table:

| Symbol | Standard | Here | Why |
|---|---|---|---|
| Private **static** field | `_camelCase` | `s_camelCase` | Distinguishes static from instance state at the use site; the runtime team's own convention |
| Private **const** | `_camelCase` | `PascalCase` | A const is not a field you mutate; PascalCase signals that |
| Generic type parameter | Single letter `T`/`K` | `T`, or `TPascalCase` when there is more than one | `TKey`/`TValue` beats `T`/`K` the moment a type has two parameters |

> §5.1 reads "Do add numeric suffixes to identifier names", which contradicts the
> row four lines below it forbidding `text1, text2, text3`. Read it as "do not".

---

## Coding style (§4.2, §6)

| Rule | Verdict | Detail |
|---|---|---|
| Braces on a new line | **Enforced** | `csharp_new_line_before_open_brace = all` |
| Always use braces, even when optional | **Deviated** (narrowly) | See the gaps section — `Entity.Equals` has four brace-less guards. Everywhere else complies |
| One namespace per file | **Enforced** | File-scoped namespaces throughout |
| One class per file | **Deviated** | Small, tightly-coupled types are grouped: `BookDtos.cs`, `BookRequests.cs`, `BookRequestValidators.cs`, `LookupConfigurations.cs`. The standard's own escape hatch — "use a descriptive file name when containing multiple" — is what we are using |
| `//` and `///`, never `/* */` | **Enforced** | Zero block comments in `src/` |
| No flowerboxing | **Enforced** | |
| One variable per declaration | **Enforced** | |
| `using` grouped, .NET first | **Enforced** | `dotnet_sort_system_directives_first`, `dotnet_separate_import_directive_groups` |
| Member order: fields → ctors → nested → properties → methods | **Mostly enforced** | `Book` declares `_isbn` beside the property it backs rather than at the top, so the rationale comment sits next to what it explains |
| Attributes stacked, one per line | **Enforced** | |
| **Tabs, size 4** | **Deviated** | `indent_style = space`, `indent_size = 4`. Spaces render identically in every viewer, and `dotnet format` and the Roslyn defaults both assume them. The standard's real goal — consistent 4-column indentation — is met |
| **`#region` around interface implementations** | **Deviated** | Zero regions in the codebase. Regions hide code from the reader while leaving it in the file, and they collapse by default in most editors, which is how dead code survives review. Our classes are small enough not to need navigation aids |
| **Copyright notice at the top of every file** | **Deviated** | `file_header_template = unset`. This is an internal assessment repository with one licence at the root. For 1Rivet client work, set `file_header_template` in `.editorconfig` and enable `IDE0073` — then the header is generated and verified rather than copy-pasted |

XML doc comments (§6.2) are **enforced** on public surface, with one change: the
standard's template carries `Method Name`, `Author`, and `Creation Date` fields.
We omit all three. Git already records author and date accurately, and a name in
a comment goes stale the moment the file changes hands. `<summary>`, `<param>`,
`<returns>`, `<exception>`, and `<remarks>` are what we write — and `<remarks>`
is where the *why* goes, which is the whole point of this project.

---

## Language usage (§7)

| Rule | Verdict | Detail |
|---|---|---|
| Built-in aliases (`int`, not `Int32`) | **Enforced** | |
| Explicit access modifiers everywhere | **Enforced** | |
| Initialise variables at declaration | **Enforced** | |
| Simplest type that fits; `int` by default | **Enforced** | |
| `decimal` for money | **Enforced** — and load-bearing | Phase 4 fines are ₹50/day. `double` would drift on accumulated fines; `decimal` is the only correct choice |
| Avoid `float`, `sbyte`, `uint`, `ulong` | **Enforced** | |
| Enum default underlying type | **Enforced** | `CopyStatus`, `CopyCondition` |
| Prefer generic collections | **Enforced** | |
| Avoid boxing | **Enforced** — analyzer-backed | CA1848 fails the build on `_logger.LogX(...)` precisely because it boxes every value-type argument |
| Avoid magic numbers | **Partially deviated** | See gaps — validator max lengths are literals duplicated from the EF configurations |
| Avoid inline string literals; use Resources | **Deviated** | Error *messages* are inline. `InvariantGlobalization` is on and the app is single-locale; the stable machine-readable contract is `ErrorCode`, not the prose. Revisit only if localisation becomes a requirement |
| `@` prefix over escaped strings | **Deviated** (one site) | See gaps |
| Never concatenate in a loop | **Enforced** | |
| `String.Format`/`StringBuilder` over concatenation | **Adapted** | We use interpolation, which §7.9 of the same document endorses. The rule targets `a + b + c`, not `$"{a}{b}"` |
| `String.Length == 0`, not `== ""` | **Adapted** | We use `string.IsNullOrWhiteSpace`. It handles null, which `.Length` throws on, and it catches the whitespace-only case that `.Length == 0` misses |
| Avoid direct casts; use `as` + null check | **Adapted** | Pattern matching does exactly this in one expression: `if (provider.GetService(t) is not IValidator validator)`. Same safety, no nullable temp |
| `?.` for null-safe access | **Enforced** | Plus `ArgumentNullException.ThrowIfNull`, which the 2019 doc could not cite |

### Flow control (§7.3)

| Rule | Verdict | Detail |
|---|---|---|
| Avoid modifying items inside `foreach` | **Enforced** | |
| Ternary only for trivial conditions | **Enforced** | Non-trivial branching uses `switch` expressions |
| Never test a bool against `true`/`false` | **Enforced** | |
| No assignment inside a conditional | **Enforced** | A pattern-match declaration (`is not IValidator validator`) is not an assignment — it is the modern spelling of the `as` + null-check this same standard requires |
| Split compound conditions into named bools | **Adapted** | Applied at three or more clauses. Naming `if (a && b)` as two locals adds words, not clarity |
| `switch`/`case` only for simple parallel logic; prefer polymorphism | **Enforced** | |
| **Avoid recursion; use loops** | **Enforced — and it decides an open issue** | Known issue #4 (category cycle detection) must walk ancestors **iteratively**. A recursive walk on a cyclic graph does not return a wrong answer, it overflows the stack — a crash reachable from a user request |
| **Avoid invoking methods within a conditional expression** | **Deviated** | `if (string.IsNullOrWhiteSpace(title))` is clearer than hoisting a bool. The rule's real target is *side-effecting* or *expensive* calls in conditions, and that we do enforce — it is the same reasoning as CA1873 |
| **§7.11 Minimize the number of returns** | **Deviated** | We use guard clauses and early returns. The standard's own example replaces three early returns with a mutable `isValid` local and a fall-through, trading a clear exit for mutable state and deeper nesting. Single-exit is a C-era rule about resource cleanup; `using`, `finally`, and the GC removed the problem it solved |

### Exceptions (§7.4)

| Rule | Verdict | Detail |
|---|---|---|
| Never use exceptions for flow control | **Enforced** | |
| Only catch what you can handle | **Enforced** | `LibraryApiFactory` catches `IOException` and `UnauthorizedAccessException` specifically, not `Exception` |
| Never an empty catch block | **Deviated** (one site) | See gaps |
| `throw;` not `throw ex;` | **Enforced** | |
| Order filters most-derived first | **Enforced** | |
| Validate to avoid exceptions | **Enforced** | FluentValidation runs before the service is reached |
| Set `InnerException` | **Enforced** | `DomainException` has the two-argument constructor |
| Derive from `Exception`, not `ApplicationException` | **Enforced** | `DomainException : Exception` |
| Suffix `Exception` | **Enforced** | |
| **Avoid defining custom exception classes** | **Deviated — deliberately, and it is structural** | See below |
| **Full Exception Constructor Pattern** (3 ctors + deserialization ctor) | **Deviated** | A parameterless `NotFoundException()` cannot produce a valid `ErrorCode`, so offering one invites an invalid instance. The pattern exists for general-purpose library exceptions that callers construct; ours are constructed in exactly the places that know the resource and key |
| **`[Serializable]` + `GetObjectData` + deserialization ctor** | **Obsolete — following it breaks the build** | `SerializationInfo`-based exception serialization is obsolete as of .NET 8 (SYSLIB0051), and `BinaryFormatter` was removed entirely. With `TreatWarningsAsErrors`, adding the deserialization constructor fails the build. There is also nothing to serialize *to*: exceptions never cross a process boundary here — they become RFC 9457 ProblemDetails JSON |
| Set `HResult` | **Obsolete** | An interop concern. Nothing here is COM-visible |
| `ComVisible(false)` on every assembly | **Obsolete** | .NET assemblies are not COM-visible unless COM hosting is explicitly enabled. The attribute would be inert |

**On custom exceptions.** The standard says use the built-in types. We define
`DomainException` with `NotFoundException`, `ConflictException`, and
`BusinessRuleViolationException` under it, because the error contract depends on
two things a built-in exception cannot provide:

1. A stable machine-readable `ErrorCode` (`loan.copy_already_on_loan`). Clients
   branch on it. `InvalidOperationException` carries only prose, and prose gets
   reworded.
2. A clean split between *"the caller asked for something the rules forbid"* (4xx,
   safe to show) and *"the system is broken"* (500, never shown). One base type
   makes that one `catch` in `GlobalExceptionHandler`. Reusing framework types
   would mean catching `InvalidOperationException` to produce a 409 — and then a
   genuine framework bug throwing the same type would be reported to the client
   as a business-rule failure.

*Rejected alternative:* built-in exceptions plus a side-channel error code.
That is a custom exception with extra steps.

The standard concedes the case itself — §7.4 continues "When a custom exception
is required..." — so this is a deviation from the preference, not from the rule.

### Events, delegates, threading (§7.5)

| Rule | Verdict | Detail |
|---|---|---|
| Null-check before invoking an event | **Adapted** | `handler?.Invoke(...)`. Relevant in Phase 4, where the notification service uses a real C# `event` |
| Derive custom `EventArgs` | **Enforced** (Phase 4) | |
| `lock()` not `Monitor.Enter()` | **Enforced** | No locking yet. On .NET 9+ prefer `System.Threading.Lock` over `lock(object)` |
| Never lock on `this` or a `Type` | **Enforced** | |
| Call `Dispose()`/`Close()`; wrap in `using` | **Enforced** | |
| Avoid finalizers; never write `Finalize()` | **Enforced** | `LibraryApiFactory` calls `GC.SuppressFinalize` in its async dispose |

> Note the difference between a C# `event` and our domain events. A C# `event`
> invokes subscribers synchronously, *before* the transaction commits — which
> would assess a fine for a return that later rolled back. Entities collect
> `IDomainEvent`s; infrastructure dispatches them after `SaveChangesAsync`
> succeeds. See `IDomainEvent` and the `domain-modelling` skill.

### Object composition and API design (§7.6, §7.7)

| Rule | Verdict | Detail |
|---|---|---|
| Explicit namespace, never global | **Enforced** | |
| Minimise `public` | **Enforced** | `private set` on every entity property |
| No `protected` in a sealed class | **Enforced** | |
| No `new` to hide members | **Enforced** | |
| `base` only in a constructor or override | **Enforced** | |
| Validate enum parameters before use | **Enforced** | `.IsInEnum()` on every enum in a request validator — an enum parameter can legally hold `(CopyCondition)99` |
| Override `==` when overriding `Equals` | **Enforced** | `Entity` overrides `Equals`, `GetHashCode`, `==`, and `!=` together |
| Prefer aggregation over inheritance | **Enforced** | |
| Avoid premature generalisation | **Enforced** | |
| Separate presentation from business logic | **Enforced** | The dependency rule makes it a compile error |
| Pattern names as class suffixes | **Enforced** | `DesignTimeDbContextFactory`, `FineRateResolver`, `ValidationFilter`, `BookRepository` |
| `virtual` only where extensibility is designed and tested | **Enforced** | `sealed` is the default |
| **Avoid more than 5 parameters (max 7)** | **Deviated** | See gaps — `Book.Create` takes 9 |
| **Always prefer interfaces over abstract classes** | **Deviated** | `Entity`, `AuditableEntity`, and `DomainException` are abstract classes because they carry *state and behaviour* — the id, the equality contract, the domain-event list — which an interface cannot. The rule is about *contracts*, and our contracts are interfaces: `IClock`, `IBookRepository`, `IUnitOfWork`, `IHasDomainEvents`. `AuditableEntity` implements `IHasDomainEvents` precisely so consumers can depend on the interface |

### Asynchronous programming (§7.12, §7.13)

**Enforced**, and we go further than the standard asks: `Async` suffix,
`CancellationToken` as the last parameter threaded to the database call, never
`async void`, never `.Result` or `.Wait()`.

---

## §9 Avoid practices — Enforced, strongly

| Rule | How it holds here |
|---|---|
| No stored procedures | Nothing in this codebase calls one |
| **No dynamic SQL** | Sorting resolves through the `BookSortOptions.SortMap` whitelist. A parameter cannot stand in for a column name, so there is no safe escaping alternative — the input is *looked up*, never used to build SQL |
| No temp tables | Aggregates are computed in SQL through LINQ projections |

---

## §10–§12 Coupling, SOLID, GoF — where they show up

| Principle | Where |
|---|---|
| **SRP** | One service per concern; the `SaveChanges` interceptor owns audit stamps so no service has to remember them |
| **OCP** | `FineRateResolver` delegate bound to configuration — changing the fine rate is a config change, not a code change |
| **LSP** | `Entity.Equals` compares `GetType()`, so a subtype never silently equals its base |
| **ISP** | `IClock` has two members. `IBookRepository` exposes only what the book use cases need |
| **DIP** | The dependency rule, enforced by project references: `Library.Domain` references nothing at all |
| **Loose coupling** | Domain defines no infrastructure types; Application declares interfaces, Infrastructure implements them |
| **Factory Method** | `Book.Create`, `Isbn.Create`, `DesignTimeDbContextFactory` |
| **Strategy** | `FineRateResolver`; the Phase 6 import dedupe strategy |
| **Observer** | Domain events, dispatched post-commit |
| **Decorator / Chain** | The middleware pipeline; `ValidationFilter` |

---

## Open compliance gaps in this repository

Verified against the current code, not assumed. Each is a small, real fix.
Tracked in TASKS.md under Phase 2.

| # | Gap | Rule | Where |
|---|---|---|---|
| 1 | `DateTime.UtcNow` in production code, 3 sites | Project rule 6 (CLAUDE.md), and it makes the rule untestable | `BookRequestValidators.cs:69,116,148` — the "publication date not in the future" checks. Should take `IClock` through the validator's constructor |
| 2 | Empty catch block | §7.4 "Never declare an empty catch block" | `LibraryApiFactory.cs` — `catch (UnauthorizedAccessException) { }` has no comment, unlike the `IOException` arm beside it |
| 3 | Brace-less `if` guards, 4 sites | §6.1 "Always use braces when optional" | `Entity.cs:41,42,46,50` |
| 4 | 9-parameter factory | §7.6 "avoid more than 5 parameters" | `Book.Create` and `Book.UpdateDetails`. A `BookDetails` parameter record would fix both and remove the argument-order hazard of eight consecutive nullables |
| 5 | Magic numbers duplicated | §7.2 "avoid inline numeric literals" | Max lengths (`500`, `50`, `4000`) appear in both the validators and the EF configurations. They must agree, and nothing makes them |
| 6 | Escaped string over `@` literal | §7.2 | `BookRequestValidators.cs` — `"^[A-Za-z0-9\\-_]+$"` should be `@"^[A-Za-z0-9\-_]+$"` |
| 7 | No explicit assembly version | §7.1 (translated: `1.0.*` does not exist in SDK projects) | `Directory.Build.props` has no `<Version>`, so every assembly is `1.0.0.0` |

`DomainEvent.OccurredAt` also reads `DateTimeOffset.UtcNow`, but that is
**structural, not a gap**: `IClock` lives in Application and `Library.Domain`
references nothing. The fix, if it matters, is to pass the instant in at the call
site rather than to give Domain a dependency.

---

## Review checklist

When reviewing C# in this repository against the standard:

1. Naming — does it match the table? Private fields `_camelCase`, statics `s_camelCase`.
2. Braces present and on their own line; four-space indentation.
3. `sealed` unless inheritance is designed for; `private set` on entity state.
4. Any new `public` member — does it need to be public?
5. Parameter count ≤ 5. If not, is there a parameter object?
6. No `DateTime.Now`/`UtcNow` — `IClock` instead.
7. No magic numbers or duplicated limits; no interpolated user input in a query.
8. Exceptions: derived from `DomainException`, stable snake-case `ErrorCode`, message safe for an unauthenticated caller.
9. Async: `Async` suffix, `CancellationToken` last and threaded through.
10. Logging via `[LoggerMessage]`, arguments hoisted (CA1848, CA1873).
11. Any deviation from the standard — is it recorded here *and* in the Decisions Log?
