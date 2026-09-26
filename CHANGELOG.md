# Changelog

All notable changes to Offramp are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may
contain breaking changes to command output; every such change is called out
under **Changed** with a migration note.

Each pull request adds its entries under `[Unreleased]`. A release moves that
block under a version heading with the date. `docs/RELEASING.md` has the steps.

## [Unreleased]

## [0.5.0] - 2026-09-26

### Added
- `offramp move tests`: finds test code in a production project semantically (test-framework
  attributes and base types; helpers by a fixpoint over which files use which, across the
  project and its dependents, requiring test-support evidence) and moves it, byte for byte, to
  its test project: `--to`, the `<Name>.Tests` naming rule, or `--create` (a new SDK-style test
  project for the detected framework, added to the solution). Each file is proven to compile in
  the destination by trial compilation and the source to compile without it; the destination
  gets the project and package references it needs and the source an `InternalsVisibleTo`.
  Dry run by default with a unified diff; `--apply` journals, stages renames with `git mv`,
  leaves project edits unstaged, verifies, and rolls back on failure. `--include-helpers`,
  `--prune-packages` (`schemas/v1/move-tests.json`).
- `offramp move rollback --journal PATH`: undoes an applied move exactly, and refuses when a file
  changed since (`schemas/v1/move-rollback.json`, `schemas/v1/journal.json`).
- `Offramp.Refactoring`: change sets (new files, edits, renames) with a unified-diff preview, a
  journal written before every step, purity checks on every rename, and rollback; project-file
  editing with Microsoft.Build's construction model (formatting, byte order mark, and line
  endings kept); solution editing.
- Diagnostics OFR2002, OFR2050, OFR2103, OFR2104, OFR2151, OFR2201–2206, and OFR2210.
- Fixture `tests-in-prod`, also added to the scan and graph snapshots.
- ADR 0018 (helper evidence, trial compilation, rollback that refuses to clobber).

## [0.4.0] - 2026-09-26

### Added
- `offramp plan`: a leaf-first migration order with each project's framework class, blast
  radius (transitive dependents), framework-only blockers, readiness, and wave (0 already
  portable, 1 portable today, n after wave n-1; cycle members share a wave). `--frontier`,
  `--for PROJECT` (the framework-only closure to port for one project), `--waves` (grouped
  view), and `--exclude-kind` (`schemas/v1/plan.json`).
- `offramp verify`: builds the selected projects with the repository's own toolchain
  (`verify.configuration`, `verify.properties`, `noWarn`, `warnAsError`, `restore`) through one
  `dotnet build` of the solution or a generated solution filter, or runs `verify.command` with
  `OFFRAMP_VERIFY_PROJECTS`/`_TARGET`/`_CHANGESET` and merges a JSON envelope it prints.
  Errors come from the binary log, grouped by code with the first occurrence; per-project
  status; `--projects`, `--affected-by PATHS` (owners plus direct dependents), `--all`,
  `--mode build|command|none`, and `--baseline` (`.offramp/verify/baseline.json`; later runs
  fail only on errors it does not list) (`schemas/v1/verify.json`,
  `schemas/v1/verify-baseline.json`).
- Diagnostics OFR5001 (verification failed), OFR5002 (timed out), OFR5010 (new error code
  relative to the baseline), OFR5020 (finding from the verification command), and OFR5090
  (verification skipped).
- A scratch work tree helper (a detached `git worktree` of `HEAD` with working-tree files
  copied over it) for trying changes without touching the user's working tree.
- ADR 0017 (plan waves, verify selection, baselines, merged findings).

## [0.3.0] - 2026-09-26

### Added
- `offramp deps audit`: for every package in use, whether each in-use version supports
  the target, the lowest and newest versions that do, the newest version, Windows-only
  assets (with the assembly and the reason), deprecation, and a known successor, with a
  status (ok, upgrade, replace, blocked, unknown) and OFR1001–1006. Feeds come from the
  repository's `nuget.config` (or `deps.feeds`); nupkg inspections are cached under
  `.offramp/cache/packages/`. `--package`, `--project`, `--include-prerelease`, and
  `--format table|json|markdown` (`schemas/v1/deps-audit.json`).
- `offramp deps gac`: .NET Framework assembly references and their modern equivalents
  (built in, a package, the Windows compatibility pack, or none), with how often source
  uses each, from compilations rebuilt from the compiler log (`schemas/v1/deps-gac.json`).
- Rule tables `rules/package-map.yml` and `rules/framework-assemblies.yml`, and
  `deps.packageMap` in `offramp.yml` to extend the first.
- `Offramp.NuGet` and `Offramp.Analysis` projects.
- Fixture `versions` (five projects, mixed package versions, a pin, `web.config` binding
  redirects) with a recorded feed (`feed.json`, `eng/record-feed.cs`) so package tests
  never depend on nuget.org.
- ADRs 0014 (how `deps audit` searches versions) and 0015 (recorded feeds).
- `offramp graph`: the project graph as data (`--format json`, `schemas/v1/graph-document.json`),
  Graphviz DOT, Mermaid, or a single self-contained interactive HTML page (layered layout,
  search, kind and framework-class filters, focus with a depth slider, clusters, cycle list,
  SVG/PNG export, light and dark). Nodes carry readiness (ready, blocked, done), their
  framework-only blockers, and dependents; `--focus`/`--depth`/`--direction`,
  `--include-kind`/`--exclude-kind`, `--cluster`, `--highlight cycles|frontier|blockers`, and
  `--edges project`. With a format and no `--out`, stdout is the document itself, ready to
  pipe into `dot`. Diagnostic OFR0201 flags Mermaid graphs above 300 projects.
- `offramp report`: the stakeholder progress page from the committed ledger and the current
  model: headline numbers, a burn-down of lines of code by framework class across scans,
  framework class by area, applications with what is left in their closure and what to port
  next, and the projects ready to port today. `--format html` (one script-free file with SVG
  charts, light and dark, print-friendly; `--with-graph` embeds the interactive graph),
  `markdown`, or `json` (`schemas/v1/report-data.json`), `--since`, and `--title`.
  Diagnostic OFR0202 names ledger files that are not snapshots. ADR 0016.

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

[Unreleased]: https://github.com/Andorbal/offramp/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/Andorbal/offramp/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/Andorbal/offramp/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Andorbal/offramp/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Andorbal/offramp/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Andorbal/offramp/releases/tag/v0.1.0
