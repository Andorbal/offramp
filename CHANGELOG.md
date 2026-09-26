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
- `offramp audit api|behavior|serialization|native`: read-only code audits driven by rule packs
  (`rules/audit-*.yml`: core, web, desktop, data, serialization, native).
  - `audit api` compiles each .NET Framework project against the target's reference
    assemblies, resolved by the SDK and NuGet in a scratch project, and reports missing APIs
    with their assembly mapping (OFR3001), Windows-only APIs (OFR3002), APIs that throw
    (OFR3003), and removed technologies (OFR3004–3009), plus a porting ledger per project and
    the top namespaces. Packages without target support are left out and named (OFR3011); a
    target that cannot be restored is OFR3010.
  - `audit behavior`: OFR3101–3120 (culture, code pages, Windows paths and time zones, the
    registry, ambient ASP.NET context, `Process.Start`, SqlClient, floating-point formatting,
    legacy networking, `app.config` runtime settings, and more).
  - `audit serialization`: BinaryFormatter and relatives (OFR3201, an error from .NET 9); each
    use classified as a transient deep clone (OFR3202) or persisted/transported data with its
    evidence (OFR3203); the types carried (OFR3204); `[Serializable]` types nothing serializes
    (OFR3205); legacy JSON and XML serializers (OFR3210, OFR3211).
  - `audit native`: P/Invoke inventory (OFR3301), ANSI string marshalling (OFR3302),
    `LibraryImport` candidates (OFR3303), COM (OFR3310), SEH interop (OFR3320).
  - `--format table|json|sarif|markdown` (SARIF 2.1.0 for code scanning), `--group-by`,
    `--all-locations`, `--pack`, `--project`. Severity overrides from `offramp.yml` mark findings
    `overridden`; each rule with findings in a project is one diagnostic, so `--fail-on` gates
    on audits. Schema: `schemas/v1/audit.json`.
- `offramp ifdef report|wrap|strip`.
  - `report`: `#if` regions and guarded lines per symbol and project.
  - `wrap --findings audit.json`: wraps the statement or member behind each `audit api`
    finding in `#if NETFRAMEWORK` (or `--symbol`), inserting whole lines only; members that
    code on every target needs are left for a real port (OFR3601), stale findings are skipped
    (OFR3602).
  - `strip --symbol S --keep true|false`: removes the regions a symbol decides, keeping the
    selected branch; regions that also depend on other symbols stay (OFR3603).
  - Dry run by default; `--apply` writes through a journal. Schemas: `ifdef-report.json`,
    `ifdef-wrap.json`, `ifdef-strip.json`.
- Fixture `behavior`: one class per audit rule with `Positive` and `Negative` members.

### Changed
- `rules/framework-assemblies.yml` maps `mscorlib` and `System.Activities`, so `deps gac` and
  `deps audit` report a mapping for them instead of `unknown`.

## [0.7.0] - 2026-09-26

### Added
- `offramp deps consolidate (--package ID | --all | --family PREFIX)`: one version per package
  across the solution.
  - Constraints come from direct references, every resolved package's dependency ranges (each
    with its chain), the chosen versions' own dependencies, and pins.
  - The version is the lowest (or `--prefer newest`) that supports every target framework of
    the projects using it.
  - Families share the highest member version. Pins keep their project on its version
    (`VersionOverride` under central package management).
  - Versions are written in place, into the existing central file, or, with `--cpm`, into a
    new one (named after the solution, with per-project opt-in, when projects outside the
    solution would inherit it).
  - Nothing is applied unless NuGet's own restore of the proposal, in a scratch worktree,
    reports no new NU1605, NU1107, NU1608, NU1010, or restore error.
  - Schema: `schemas/v1/deps-consolidate.json`.
- `offramp redirects sync [--app PROJECT] [--prune]`: binding redirects for .NET Framework
  applications, computed from the assemblies the resolved packages deploy.
  - Redirects are added, changed, or (with `--prune`) removed entry by entry; every other byte
    of `web.config`/`app.config` stays.
  - Schema: `schemas/v1/redirects-sync.json`.
- `offramp deps resolve-dlls [--project PROJECT]`: loose `HintPath` references become a
  `ProjectReference` (the DLL is a project's output) or a `PackageReference`. The package must
  ship the assembly with the same public key, at the referenced version or higher, for every
  target framework. .NET Framework DLLs with no replacement are reported as blockers
  (`schemas/v1/deps-resolve-dlls.json`).
- Package inspection records dependency groups and assembly identities (cache format 2).
- Diagnostics OFR1200, OFR1203, OFR1210–1212, OFR1220, OFR1401–1404, and OFR1501–1504.
- Fixtures `loose-dlls` (stub DLLs from a generator) and `cpm-shadowing`, both in the scan
  snapshots.
- ADR 0020 (consolidation decides and restore verifies, the opt-in import, loose DLL candidates,
  redirect rules).

### Changed
- Unified diffs print removed lines before added ones, as git does.

### Fixed
- Commands no longer wait indefinitely for the output of a `dotnet` or `git` process that has
  exited while a process it started (an MSBuild node, the compiler server) still holds its
  output pipes. Output is drained for at most ten seconds after exit, and MSBuild node reuse
  is off for the processes Offramp runs.
- Scratch copies (`verify`, restore verification) live under the canonical temporary directory,
  so paths in tool output map back to the repository on macOS, where `/var` is a link to
  `/private/var`.

## [0.6.0] - 2026-09-26

### Added
- `offramp move plan --from SRC --to DEST (--files GLOB... | --files-from LIST | --all)`: a
  deterministic, reviewable plan for moving files between projects, with no repository
  changes. It partitions what each file uses with the semantic model, brings along what it
  needs (`--co-move closure`, or excludes it with `none`), and proposes project and package
  references. Cycles are rejected with their path, and packages without assets for the
  destination keep the file. Each file is proven to compile in every destination target
  framework, the source to compile without it, and projects depending on the source to still
  see moved types. Windows-only APIs are reported with the destination's own CA1416 analyzer.
  Partial types and resource pairs move together, destination `Compile Remove` patterns are
  respected, and namespace mismatches can warn or block. `--out` writes the plan
  (`schemas/v1/move-plan.json`; result `schemas/v1/move-plan-result.json`).
- `offramp move apply --plan PATH`: checks the workspace hash and every file's hash, journals
  each step, performs project edits then pure renames (`git mv`), and verifies per the policy.
  - Policies: `none`, `end`, `per-project`, or `batch:N`. Batches never split files that
    need each other.
  - On failure it rolls the whole run back, or with `--on-failure keep` leaves it for
    `--resume`, which finishes an interrupted journal.
  - `--force` applies a plan made from another workspace model.
  - Result schema: `schemas/v1/move-apply.json`.
- `offramp forwarders --from SRC --to DEST [--since REF] [--apply]`: writes `TypeForwarders.cs`
  in the source for public types that now live in the destination (read from the last scan's
  compilation, or from the source at a commit), and adds the source's reference to the
  destination. It reports strings that name moved types with the old assembly, in C# string
  literals and configuration or data files (`schemas/v1/forwarders.json`).
- Diagnostics OFR2001, OFR2003–2005, OFR2101, OFR2102, OFR2105, OFR2110, OFR2111, OFR2120,
  OFR2150, OFR2152, OFR2301, and OFR2302.
- Fixture `move-cases` (one case per planning rule, also in the scan snapshots) and a generated
  500-file `hollow` fixture for the overnight `--all` run.
- ADR 0019 (needs and batches, whole-run rollback, resumable journals, dependents, forwarders
  from the scan).

### Changed
- Journals (`schemas/v1/journal.json`) record the bytes each create or edit step writes
  (`after`) and the plan they apply (`plan`), so an interrupted run can be finished.
- A file changed between planning and applying now fails with a journal conflict instead of a
  purity violation (the rename never happens either way).

### Fixed
- `scan` no longer records the ProjectReference items the SDK adds for transitive references
  (which logs made on Windows keep with the evaluation) as a project's own references: only the
  references restore saw declared (the assets file's restore metadata) are kept.

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

[Unreleased]: https://github.com/Andorbal/offramp/compare/v0.7.0...HEAD
[0.7.0]: https://github.com/Andorbal/offramp/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/Andorbal/offramp/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/Andorbal/offramp/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/Andorbal/offramp/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Andorbal/offramp/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Andorbal/offramp/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Andorbal/offramp/releases/tag/v0.1.0
