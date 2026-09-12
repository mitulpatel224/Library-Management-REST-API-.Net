---
description: Start a phase — review scope, create the branch, and confirm the plan before writing code.
argument-hint: "[phase number, e.g. 3]"
---

Start work on **phase $1** of the Library Management API.

Do these in order, and **stop at step 5 for confirmation** before writing any code.

1. **Read the scope.** Open `TASKS.md` and list every unticked item for phase $1.
   Also read the previous phase's guide in `docs/phases/` so you build on what
   exists rather than reinventing it.

2. **Check the decisions log** at the bottom of `TASKS.md`. Some of phase $1's
   design is already decided — do not relitigate it. Flag anything that now looks
   wrong rather than silently departing from it.

3. **Check the open issues** table. Confirm none of them block this phase.

4. **Create the branch:**
   ```bash
   git switch -c phase/0$1-<short-name>
   ```
   Confirm the working tree is clean first.

5. **Summarise and stop.** Present:
   - the endpoints or capabilities this phase adds;
   - the entities and migrations required;
   - the concepts this phase exists to demonstrate;
   - any decision you need from the user before starting;
   - the order you intend to build in.

   Then **ask which piece to build first.** The user validates incrementally and
   wants to understand each change before the next lands.

Do not write code until the user answers.
