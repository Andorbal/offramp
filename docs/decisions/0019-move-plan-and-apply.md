# 0019. Move plan and apply: dependency-closed batches, whole-run rollback, resumable journals, forwarders from the scan

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/move.md#move-plan`, `#move-apply`, `#forwarders`

## Context

The spec names `move apply`'s verification policies (`per-project`,
`batch:N`) without defining them. It says a failure "undoes from the journal"
without saying how much. It asks `--resume` to continue a journal that, as M4
wrote it, did not hold the bytes still to be written. It leaves open:
- how a plan's project edits are shaped
- what happens to projects that depend on the source and use moved types
- where the CA1416 analyzer's options come from for files at new paths
- what "SRC's last built assembly" means for `forwarders` once verification
  has rebuilt it
- which diagnostics a forwarders cycle and an unknown `--since` produce

## Decision

- **Plans record needs.** Every planned move lists the planned files that must
  move no later than it: those it uses, or the files that use it when the
  destination already depends on the source; resource pairs need each other.
  `move apply` uses this to skip, along with a changed file, everything that
  needs it (`OFR2150`), and to form batches.
- **`batch:N`** applies strongly connected groups under `needs`, each after
  the groups it needs (Tarjan's algorithm over path-sorted files). Groups are
  packed into batches of about N files, so every verified state is one that
  should compile. **`per-project`** runs one verification per touched project
  in dependency order, after all steps, and stops at the first failure.
  **`end`** runs one verification. All policies verify the source, the
  destination, and their direct dependents.
- **Rollback undoes the whole run**, not just the failing batch: the
  repository returns to the state the plan was made against, and exit 1 means
  nothing moved. **`keep` stops at the failing batch** and leaves the journal
  `applying`, so `--resume` continues once the cause is fixed.
- **Journals are self-contained.** Create and edit steps record the bytes they
  write (`after`), and the journal records the plan it applies (`plan`).
  Resuming picks the newest `applying` journal of the same plan and continues
  it. A step whose result is already there (performed but not recorded before
  an interruption) is only marked done. Any other mismatch stops with
  `OFR2152`. Resuming verifies once, and `batch:N` becomes `end`.
- **Project edits have one shape**, `{project, kind, value, version}`, as in
  `move tests`. `keepResourceName` keeps a moved `.resx`'s manifest name. It is
  an `EmbeddedResource Update` with a `LogicalName` in the destination, merged
  with the Designer metadata that follows the file.
- **Dependents keep compiling.**
  - A project referencing the source that uses moved types sees them through
    the source's new reference to the destination when it is SDK-style
    (transitive references).
  - Otherwise, or when the destination already depends on the source, it gets
    its own reference.
  - A dependent that would close a cycle, or has no compatible destination
    target, keeps the files it uses (`OFR2001`, `OFR2104`).
- **CA1416 sees the destination's MSBuild properties everywhere.** The
  recorded analyzer options are keyed by the recorded syntax trees. A trial
  compilation holds different tree objects, including the moved ones, so the
  loader answers every tree with the recorded global options
  (`build_property.TargetFramework`, `_SupportedPlatformList`, ...).
  Per-folder `.editorconfig` severities for the new paths are not applied.
- **Forwarders read the former surface from the scan.**
  - Without `--since`, the source's public top-level types come from the last
    scan's compiler log. `bin/` is rebuilt by `move apply`'s verification, so
    it already lacks them.
  - With `--since`, they come from the source folder's files at that commit.
  - The types found now are read from C# files on disk (syntax only). The
    workspace model is stale after a move, and a declaration's name needs no
    semantic model.
  - A cycle writes nothing and is `OFR2001`, a warning like its other uses. A
    ref that is not a commit is `OFR2302`.
  - `--apply` journals the change, so `move rollback` undoes it.
- **`move plan --out` writes the plan**, not the envelope; the result carries
  `output`.

## Alternatives considered

- Rolling back only the failing batch: it keeps progress, but the exit code
  would then describe a half-applied plan, and the next run needs a new plan
  anyway. `keep` plus `--resume` covers "keep what worked".
- Batching by plan order: a file can land in a batch before a file it needs,
  so a verification fails for a reason the plan would not have.
- Re-planning on resume instead of journaling contents: the project files are
  half-edited by then, so a fresh plan cannot reproduce the original edits.
- Reading `bin/` for the former surface: see above.

## Consequences

- An overnight `--all` run over 500 files completes with one verification
  (`MovePlanCommandTests.Hollowing_out_500_files_verifies_once_at_the_end`).
- Journals now grow with the size of the project files they edit. They stay
  small next to the files they move, which are not copied.
- A hand-edited plan that drops a file others need still applies. Verification
  catches it, and `batch:N` skips nothing it does not know about.
