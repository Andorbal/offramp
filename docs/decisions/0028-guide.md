# 0028. A guide that asks instead of deciding

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/guide.md`, `docs/spec/01-cli-conventions.md#interactivity`

## Context

Offramp has a command for every step of an incremental migration, but someone
who has never migrated a .NET Framework codebase does not know which steps
exist, in what order they make sense, or which apply to their repository. The
request was an `offramp guide` that walks people through as many steps as it
can, remembers what has been done in `.offramp/`, runs `doctor` first on a
fresh repository, and asks whenever more than one thing could come next,
without trying to be smart or to migrate the application end to end.

Open questions: how the guide decides what is next, what it records and
where, how it runs other commands, how it squares with "no destructive
operation without `--apply`" and "no command requires a TTY", and what it
returns with `--json`.

## Decision

- **A fixed checklist, not a planner.** The steps are an explicit list in code
  (`Offramp.Workspace/Guide/GuideCatalog.cs`), in four stages (get set up, see
  what you have, tidy up on .NET Framework, port a wave at a time). Each step
  names one Offramp command, a paragraph on why it matters, what it writes, the
  steps it requires, and at most one simple fact that decides whether it
  applies (a project with Windows-only build steps, a non-SDK project, a
  package at two versions, a web project, ...). Facts come from the workspace
  model, `offramp.yml`, and `Directory.Build.props`; nothing is inferred from
  source code.
- **Ask when there is a choice.** The current stage is the first with an open
  step; its open steps are the choices. One open step is offered alone; several
  are listed and the user picks. Later stages wait, so the guide never jumps
  ahead, and a stale model brings the user back to `scan`.
- **Observed beats recorded for a few steps.** `init`, `scan`, `compile-only`,
  and `port` are done when the repository shows it, so work done outside the
  guide counts. Everything else is done when the guide ran it successfully or
  the user said so. `scan` cannot be skipped: every later fact needs the model.
- **Progress lives in `<state>/guide.json`**, a sorted list of records (step,
  project, status, exit code) with no timestamps or absolute paths, so it is
  deterministic and can be committed. It is Offramp's state and written without
  `--apply`. An unreadable file is reported and left alone.
- **Steps run in process through the same command tree** (`OfframpCli.RunAsync`),
  as `mcp serve` does, so a step behaves exactly as if typed and the guide
  never reimplements a command. With `--json`, the step runs with `--json` and
  its envelope is embedded in the guide's result.
- **Writers stay dry runs unless the guide gets `--apply`.** Without it, a
  step that changes the repository shows its dry run and the command that would
  apply it. With `--apply`, a session asks after a clean dry run and applies on
  yes. `init` keeps its own contract (it writes `offramp.yml` unless
  `--dry-run`).
- **Every prompt has a flag.** `--run`, `--done`, `--skip`, `--reset`, and
  `--project` do one thing each without a terminal, which also makes the guide
  an MCP tool (`offramp_guide`) for agents. The CLI conventions now name `init`
  and `guide` as the two conversational commands.

## Alternatives considered

- **Rank the next steps** (by blast radius, audit counts, package status). It
  would make the guide opinionated in ways that are hard to explain and to
  test, and the request asked for the opposite.
- **Rules as a YAML data file** (`rules/guide.yml`). The steps' conditions are
  code over the model; a YAML version would need a small expression language
  for a list that only Offramp's own commands change.
- **Record every command run anywhere** (hook the command runner). Every command
  would write guide state, including in CI. Observing the few facts that matter
  gives the same answer for the steps where it matters most.
- **Timestamps in the progress file.** Useful for "when did we scan", but the
  model and ledger already carry that, and the file would change on every run.
- **Apply after an interactive "yes" without `--apply`.** Friendlier, but it
  would make the guide the one way to change the repository without the flag.

## Consequences

- New commands only reach newcomers once they are added to the catalog; the
  guide test lists every step, so the list is reviewed with each change.
- The guide cannot tell that a writer's change was applied outside it unless
  the fact behind the step changes (for example, no legacy project is left);
  such steps are marked done by hand.
- Per-project steps can be long lists on large repositories; the session asks
  for the step first and the project second.
