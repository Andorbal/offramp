# 0010. Scan logs captured in another checkout or on another machine

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/02-workspace-model.md#inputs`, `docs/compiling-on-macos.md`

## Context

The spec lets `scan` read a binary log (`--binlog`) or a compiler log
(`--complog`) captured anywhere, typically on a Windows agent, but leaves open
how absolute paths from that machine map onto this checkout, what a compiler
log alone can provide, and what happens to files the log refers to but this
machine lacks.

## Decision

- **Paths.** The capture root is inferred from the logged project paths: the
  longest prefix whose remainder names a project file that exists in this
  checkout (majority vote, ordinal tie-break). Windows and macOS captures, and
  Windows-style paths anywhere, compare case-insensitively. Paths outside the
  capture root are outside the repository.
- **`--binlog` and `--complog` together** give the full model: evaluation from
  the binary log, compiler calls from the compiler log. The compiler log is
  copied to `.offramp/build.complog` so the model's references stay valid.
- **`--complog` alone** gives the reduced model the compiler invocations
  support (targets, compile items, project references, assembly names, define
  constants) and reports `OFR0103`: no packages, SDK, test detection, or
  Windows-only steps.
- **A binary log from elsewhere** is not converted to a compiler log here: the
  conversion reads the referenced files from disk, so its output would depend on
  what this machine happens to have. Scan reports `OFR0132` and the projects
  have no compiler calls until a compiler log from the capturing machine is
  supplied.
- **Assets files** (`project.assets.json`) are read from this checkout. When a
  project's is missing, scan reports `OFR0104` and leaves `resolved` empty;
  `dotnet restore` fixes it.

## Alternatives considered

- Asking for the capture root as an option: every user would need to know it;
  inference from project paths is exact for any log of this repository.
- Converting foreign binary logs best-effort: the result depends on the
  scanning machine's package cache, so two developers would get different models.

## Consequences

- CI proves the round trip: a `windows-capture` job scans `dual-target` on
  Windows, and every OS compares the model built from those logs with its native
  model (roadmap M1 acceptance).
