---
description: Close out a phase — verify everything, write the concept guide, update the tracker, and commit.
argument-hint: "[phase number, e.g. 2]"
---

Close out **phase $1**. Do not skip a step, and do not report success for
anything you have not actually run.

## 1. Verify the build

```bash
dotnet build LibraryManagement.slnx -c Release
```

Must be **0 warnings, 0 errors**. If an analyzer fires, fix the code — do not
suppress the rule.

## 2. Run the tests

```bash
./tests/Library.UnitTests/bin/Release/net10.0/Library.UnitTests.exe
./tests/Library.IntegrationTests/bin/Release/net10.0/Library.IntegrationTests.exe
```

(`dotnet test` is still blocked — issue 1 in `TASKS.md`.)

Report the real counts. If something fails, say so with the output.

## 3. Exercise the endpoints

Start the API and actually call every endpoint this phase added. For each:

- happy path returns the documented shape;
- not-found returns 404 with the right `errorCode`;
- invalid input returns 422 with a per-field `errors` object;
- conflicts return 409;
- it appears in `/swagger` with all response types.

Watch the logged SQL and confirm no N+1: one listing request should not produce
twenty queries.

## 4. Write the concept guide

`docs/phases/phase-0$1.md`, following the shape of the existing guides:

- **What we built** — the deliverables.
- **Concepts** — for each: *what it is → why here → the alternative rejected →
  what it costs*. This is the part the assessment is actually about.
- **Things that bit us** — real bugs hit, with the cause and the fix. These are
  the most useful sections in the whole document; do not sanitise them.
- **SOLID in this phase** — specific places, not a checklist.
- **Verification** — runnable commands with expected output.
- **Questions you should be able to answer** — interview-style.
- **What the next phase builds on this.**

## 5. Update the other documentation

- `docs/api-contract.md` — new endpoints, parameters, error codes, **real**
  example responses copied from actual calls.
- `docs/data-model.md` — new tables, indexes, delete behaviours.
- `docs/data-flow.md` — a sequence diagram for any non-trivial new flow.
- `README.md` — the API-surface table and the roadmap row.

## 6. Update the tracker

In `TASKS.md`:

- tick completed items — only where code **and** docs are done;
- add any discovered work as new items rather than widening an existing one;
- add a Decisions Log row for every non-obvious choice, with the date, the
  reasoning, and the option rejected;
- update the open-issues table.

## 7. Regenerate the diagrams

If Graphify and Archify are installed:

```bash
graphify .
```

Then regenerate any Archify diagram this phase invalidated. **Read the output
before committing it** — the agent authored the IR and can get an edge wrong.
Check the knowledge graph for edges leaving `Library.Domain`: one means the
dependency rule has been broken.

## 8. Commit

Stage, then write a conventional commit describing **what changed and why** —
not a file list. Include any decision worth recording and any known gap.

End the message with:

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

## 9. Report

Summarise for the user:

- what now works, and how they can verify it themselves;
- what you fixed along the way;
- what is deliberately still missing;
- anything you were unsure about.

Then **ask before starting the next phase.**
