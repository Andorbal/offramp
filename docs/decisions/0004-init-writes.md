# 0004. Let `init` create files without `--apply`, never overwrite without `--force`

- Status: accepted
- Date: 2026-09-25
- Spec section: `docs/spec/03-configuration.md#init`, `docs/spec/01-cli-conventions.md`

## Context

CLAUDE.md forbids destructive operations without `--apply`/`--yes`, and the
global `--dry-run` is "on for writers". The spec also says `init --defaults`
"writes the detected configuration without asking". Solution detection and the
exact `.gitignore` entries are unspecified.

## Decision

- `init` is not a writer in the dry-run-by-default sense: it creates
  `offramp.yml` and appends `.gitignore` entries, which destroys nothing. It
  never overwrites an existing `offramp.yml` unless `--force` is given
  (`OFR0030`, exit 1, otherwise). `--dry-run` previews the file as a unified diff.
- On a terminal (stdin and stdout), without `--defaults`, `--yes`, or `--json`,
  `init` interviews: target, solution, verify mode, CPM file, and pins.
- Solution detection searches the repository (skipping hidden, `bin`, `obj`,
  `node_modules`, `packages`, `artifacts`, `TestResults`) for `.sln`, `.slnx`,
  and `.slnf`. It picks the only `.sln`/`.slnx`, else the only one at the root;
  solution filters are never chosen automatically. Otherwise `solution:` stays
  empty and `OFR0020` is reported as a warning.
- `.gitignore` gains `<state>/cache/`, `<state>/journal/`, `<state>/verify/`,
  `<state>/*.binlog`, `<state>/*.complog` (the spec's four plus the verify binlogs
  directory), each only if not already present, under one comment line. The
  ledger stays committed.
- The written YAML is checked by a test that loads it back through the
  configuration loader with no diagnostics.

## Alternatives considered

- Requiring `--apply` for `init`: rejected; it contradicts the spec's
  `init --defaults` and protects nothing, since nothing is overwritten.

## Consequences

- `init --force` is the one path that replaces user content, and it must be typed.
