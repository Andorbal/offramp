# 0002. Fix the CLI execution path, exit-code precedence, and output routing

- Status: accepted
- Date: 2026-09-25
- Spec section: `docs/spec/01-cli-conventions.md`

## Context

The conventions define global options, exit codes, the envelope, and progress
modes, but leave open: how the repository root is found, which exit code wins
when several apply, where `--out` sends output when `--json` is off, how an
invalid `offramp.yml` or an internal bug surfaces, and when each progress
renderer is used.

## Decision

Every command runs through one `CommandRunner`:

1. **Repository root**: the git work-tree top (`git rev-parse --show-toplevel`);
   otherwise the nearest ancestor containing `offramp.yml`; otherwise the working
   directory.
2. **Configuration** loads before the handler (ADR 0003). An invalid file stops
   the command with exit **2** (a usage error: the invocation is ambiguous), except
   for commands that exist to report or replace configuration (`doctor`, `init`).
3. **Progress**: `--quiet` → nothing; `--json` → NDJSON on stderr; stderr is a
   terminal → Spectre live display (never for commands that prompt); otherwise one
   plain line per phase. NDJSON `progress` events are throttled to 10 per second
   per phase; the final one (`current == total`) is always written.
4. **Output**: with `--json` the envelope goes to `--out` if given, else stdout.
   Without `--json` the human rendering goes to stdout, and `--out` additionally
   receives the envelope (commands with a primary artifact, such as `graph`,
   write that artifact instead). When a command produced no result, the human
   explanation goes to stderr so redirected stdout stays clean.
5. **Exit-code precedence**: usage (2) > environment (3) > partial (4) >
   findings at or above `--fail-on` (1) > success (0). Ctrl-C is 130.
6. **Internal errors** (unexpected exceptions) are reported as `OFR0099` and exit
   **3**: the exit-code table has no "bug" code, and 3 already means "could not
   run, not your findings". `--verbose` adds the stack trace on stderr.
7. **Colors** are off for `--json`, redirected output, `NO_COLOR`,
   `OFFRAMP_NO_COLOR`, and `TERM=dumb`.
8. `--version` prints the MinVer version without build metadata.

## Alternatives considered

- Exit 1 for invalid configuration: rejected; 1 means "ran and found problems",
  which a script cannot tell apart from real findings.
- A dedicated exit code for internal errors (for example 70): rejected to keep
  the documented table closed; the `OFR0099` diagnostic distinguishes bugs.
- Spectre progress even when stderr is redirected: rejected; control sequences
  in CI logs are noise.

## Consequences

- Handlers never touch stdout, stderr, or exit codes; tests drive the runner
  with fake hosts and assert all of it.
- Adding a command means writing a handler and a renderer; the envelope,
  progress, and exit behavior come for free.
