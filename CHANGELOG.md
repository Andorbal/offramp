# Changelog

All notable changes to Offramp are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may
contain breaking changes to command output; every such change is called out
under **Changed** with a migration note.

Each pull request adds its entries under `[Unreleased]`. A release moves that
block under a version heading with the date. `docs/RELEASING.md` has the steps.

## [Unreleased]

### Added
- `offramp graph`: the project graph as data (`--format json`, `schemas/v1/graph-document.json`),
  Graphviz DOT, Mermaid, or a single self-contained interactive HTML page (layered layout,
  search, kind and framework-class filters, focus with a depth slider, clusters, cycle list,
  SVG/PNG export, light and dark). Nodes carry readiness (ready, blocked, done), their
  framework-only blockers, and dependents; `--focus`/`--depth`/`--direction`,
  `--include-kind`/`--exclude-kind`, `--cluster`, `--highlight cycles|frontier|blockers`, and
  `--edges project`. With a format and no `--out`, stdout is the document itself, ready to
  pipe into `dot`. Diagnostic OFR0201 flags Mermaid graphs above 300 projects.

### Changed
- `offramp slice` without `--out` now writes only the solution filter to stdout (diagnostics go
  to stderr), so `offramp slice --for Foo > foo.slnf` works.

### Fixed
- Concurrent scans in one process could read an empty build from a binary log
  (MSBuild.StructuredLogger returns each read's result through a static field); reads are now
  serialized.

## [0.2.0] - 2026-09-26

### Added
- `offramp scan`: builds the solution with a binary log (or reads `--binlog`,
  `--binlog` with `--complog`, or `--complog` alone), converts it to a compiler
  log, and writes the workspace model (`.offramp/workspace.json`,
  `schemas/v1/workspace.json`) and a ledger snapshot (`schemas/v1/ledger.json`).
  The model has project kinds with evidence, framework classes, packages and
  the resolved package graph from `project.assets.json`, assembly and COM
  references, define constants per target, Windows-only build steps, and the
  project graph with cycles and a leaf-first order. `--no-build` reuses the last
  log; `--if-stale` rescans only when needed (`schemas/v1/scan.json`).
- Logs captured on another machine or checkout (for example a Windows agent)
  are mapped onto the local checkout; CI proves a model built from Windows logs
  of `dual-target` equals the native one on ubuntu, macOS, and Windows.
- `offramp slice`: writes a solution filter (`.slnf`) or a SlnGen command for a
  project closure, with `--include-dependents` and `--include-tests`
  (`schemas/v1/slice.json`).
- `offramp doctor`: checks for model freshness, Windows-only build steps, and
  central package management hazards; `--fix` previews and `--fix --apply`
  writes the compile-only block to `Directory.Build.props`. `init` offers the
  same block on a terminal when the model shows Windows-only steps.
- `--fail-on-stale` global option: a stale model is an error instead of a warning.
- Diagnostics OFR0002–0004, OFR0021, OFR0022, OFR0101–0104, OFR0110–0115,
  OFR0120, OFR0130–0132, and OFR1301–1303.
- Fixtures `netfx-only`, `dual-target`, `cycle`, and `windows-only-build-steps`
  (with a committed binary log), each snapshot-tested.
- ADRs 0007–0012: log-reading libraries, no `scan --fast`, staleness by content
  hash, scanning logs from elsewhere, machine-independent model rules, and
  `doctor --fix`/`slice` behavior.

### Changed
- `doctor` reports two more checks (`windows-only-build-steps`, `cpm`) and a
  `fix` field; `init` results carry `compileOnlyFix`. Consumers that assert on
  the exact check list need the two new ids.
- The envelope's `solution` shows the solution the model was built from when
  none is configured.
- The workspace model schema adds `inputs`, `source.complog`, and per project
  `language`, `defineConstants`, `packagesConfig`, and `partial`
  (`docs/spec/02-workspace-model.md`).

## [0.1.0] - 2026-09-25

### Added
- Handoff pack: README, contributor contract (CLAUDE.md), specification
  (`docs/spec/`), roadmap, release process, CI and release workflows.
- Solution scaffold: `Offramp.slnx`, `global.json` (.NET 10 SDK), central package
  management, `.editorconfig`, `.gitattributes`, and `Offramp.Core`,
  `Offramp.Workspace`, `Offramp.Cli` with one test project each plus
  `Offramp.Fixtures` for shared test support.
- `offramp` global tool (`net8.0;net10.0`, `RollForward=LatestMajor`) built on
  System.CommandLine and Spectre.Console: all global options, exit codes
  (0/1/2/3/4/130), the JSON envelope (`schemas/v1/envelope.json`), NDJSON progress
  on stderr with `--json` (`schemas/v1/progress.json`), a live progress display on
  terminals, plain progress lines otherwise, and `NO_COLOR`/`TERM=dumb` support.
- `offramp doctor`: checks the .NET SDKs, `global.json` selection, the target,
  .NET Framework reference assemblies, git, the repository, `offramp.yml`, and the
  workspace model, each with a remedy (`schemas/v1/doctor.json`).
- `offramp init`: writes `offramp.yml` with detected values (interview on a
  terminal, `--defaults` without), adds Offramp's state to `.gitignore`, previews
  with `--dry-run`, and never overwrites without `--force` (`schemas/v1/init.json`).
- `offramp --version` and `--help` with examples for every command.
- `offramp.yml` loading with the documented precedence (defaults, file,
  `OFFRAMP_*`, flags), validation against `schemas/v1/config.json`, positions on
  every configuration diagnostic, and the merged `effectiveConfig` in every envelope.
- Diagnostic catalog with codes OFR0001, OFR0010–0016, OFR0020, OFR0030,
  OFR0050–0056, OFR0099, and OFR1006; `docs/diagnostics.md` is now generated from
  it (`eng/gen-diagnostics.sh`), and tests fail when a code lacks documentation or
  a test that produces it.
- Workspace model records (`docs/spec/02-workspace-model.md`), `ICache`,
  `IGitService` (`git mv`, porcelain status), and a git-compatible unified diff
  renderer for dry runs.
- ADRs 0002–0006: CLI foundations, configuration loading, `init` writes, test
  stack, and the doctor contract.

### Changed
- `Directory.Build.props` no longer produces a separate symbols package: PDBs
  are embedded, so `dotnet pack` failed with NU5017 when asked for a `.snupkg`.

[Unreleased]: https://github.com/Andorbal/offramp/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Andorbal/offramp/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Andorbal/offramp/releases/tag/v0.1.0
