# 0012. `doctor --fix` writes only with `--apply`; how `slice` resolves projects

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/workspace.md#doctor`, `docs/spec/commands/workspace.md#slice`

## Context

The spec says `doctor` is read-only and that `--fix` "writes [the compile-only
conditional] to `Directory.Build.props` after showing the diff", which conflicts
with non-negotiable 7 (no write to the user's repository without
`--apply`/`--yes`). It also leaves open how `slice --for` names projects and how
a stale model is treated by commands other than `scan`.

## Decision

- `doctor --fix` shows the unified diff of the compile-only block
  (`docs/compiling-on-macos.md`) against the root `Directory.Build.props` and
  writes nothing; `doctor --fix --apply` writes it, asking first on a terminal
  unless `--yes`. The block is inserted as text before the last `</Project>`,
  keeping every other byte (encoding, BOM, line endings, formatting); the file is
  created when missing; a second run finds the `<OfframpCompileOnly>` marker and
  changes nothing. The result carries `fix: { file, alreadyPresent, applied, diff }`.
- `init` offers the same block (never by default) when the model shows
  Windows-only build steps; `--defaults` never adds it.
- Doctor gains the checks `windows-only-build-steps` (from the model's
  `OFR0110`–`OFR0115`) and `cpm` (`OFR1301`–`OFR1303` against the model's
  projects, or the solution when there is no model), and `workspace` now also
  checks freshness (`OFR0002`).
- `slice --for` accepts repository-relative paths, paths relative to the
  working directory, and project names (unique match, case-insensitive), comma
  separated or repeated. An unknown project is a usage error (`OFR0021`, exit 2).
  The `.slnf` stores the solution path relative to the filter file and project
  paths relative to the solution with backslashes, as Visual Studio writes them.
- `--fail-on-stale` is a global option: every command that reads the model
  reports a stale model as `OFR0002` at error severity instead of warning.

## Alternatives considered

- Writing on `--fix` alone, as the spec's wording suggests: breaks
  non-negotiable 7.

## Consequences

- `offramp doctor --fix --apply` is the remedy doctor and scan print for
  Windows-only steps.
