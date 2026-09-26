# 0017. Plan waves, verify selection, baselines, and merged findings

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/workspace.md#plan`, `#verify`

## Context

The `plan` spec asks for "waves where every project in a wave depends only on
earlier waves" without saying where portable projects and cycles go, or whether
`wave` is part of the JSON without `--waves`. The `verify` spec leaves open:
- how one build covers a selection of projects
- how changed paths map to projects
- what a baseline compares, and whether a run with only known errors passes
- what `--baseline` does to the verdict
- how a script's envelope with non-Offramp codes fits a contract that requires
  `OFR####` codes

## Decision

### plan

- **Waves.** Portable projects (standard, modern, dual) are wave 0. A
  framework-only project's wave is one more than the latest wave among its
  framework-only blockers outside its own cycle. So wave 1 is the frontier,
  cycle members share a wave, and every framework-only dependency sits in an
  earlier wave or the same cycle. A property test checks this on every fixture,
  and a negative test checks that the property check can fail.
- **Order** is by wave, then blast radius (largest first), then path. Within a
  wave no project depends on another, so porting the most depended-on first
  unlocks the most.
- **`wave` is always in the JSON**; `--waves` only groups the human view.
- **`--for`** lists the framework-only projects in the closure, which is the
  same rule `report` uses for applications (`0016`).

### verify

- **One build per run.** When everything is selected, build the solution. For a
  subset, write `.offramp/verify/verify.slnf` over the solution (or over the
  solution a `.slnf` model filters) and build that, so MSBuild parallelizes and
  builds references as usual. Only without any solution are projects built one
  by one.
- **Affected paths** map as the spec now lists: project files, compile items,
  build-wide files (`.props`, `.targets`, `global.json`, `nuget.config`) to
  everything beneath their folder, and other paths to their folder's project.
  Direct dependents are added, as the spec says. This errs toward building
  more.
- **Baseline identity** is code, project, file, and message, without line or
  column, so edits elsewhere in a file do not turn known errors into new ones.
  A run passes when every counted error is in the baseline.
- **Failures are never silent.** A failed process always contributes at least
  one error: from the binlog, else its last output lines, or "verify.command
  exited with code N". So a crash the baseline never saw cannot pass.
- **`--baseline` accepts the current state.** It records the errors and judges
  the run against them, so recording passes unless the run timed out.
- **Per-project status** is `failed` for projects with counted errors. Otherwise
  it is `passed` when the run passed and `notVerified` when it did not, because
  a dependent of a failed project never compiled.
- **Merged findings** keep `OFR` codes as they are. Any other code is reported
  as `OFR5020` at its own severity, with the original code in `data.code`, so
  every envelope diagnostic still has a stable Offramp code. The tool's own code
  is kept in the verify result's error groups, as compiler codes are.
- **The baseline lives in `.offramp/verify/`**, which `init` already ignores.
  Teams that want a shared baseline commit it with `git add -f`. This avoids
  changing `init`'s `.gitignore` contract.

## Alternatives considered

- Building each selected project separately: slower, and builds shared
  dependencies again for every project.
- Baseline identity by code only: one known CS0246 would hide every new CS0246.
- Relaxing the envelope schema to allow any code: consumers could no longer rely
  on `OFR####` or the help URL.

## Consequences

- Movers (M4, M5) call `VerifyRunner` with their affected projects and a change
  set path. `ScratchWorktree` (a detached `git worktree` of `HEAD` with
  working-tree files copied over it) is ready for consolidation's restore check
  (M6).
