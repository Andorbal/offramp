# Roadmap

Ordered milestones. Each is independently shippable, ends with a release
(`docs/RELEASING.md`), and lists acceptance criteria that a pull request must
meet. Work top to bottom; within a milestone, commands may be split into
separate pull requests. The specification for every command is under
`docs/spec/commands/`.

Status legend: ☐ not started · ◐ in progress · ☑ released

## M0 — Foundations ◐

Scaffold the solution and everything a release needs, with one working command.

- Solution layout per `CLAUDE.md`; `global.json`; `Directory.Build.props`
  already present; `.editorconfig`; `eng/` scripts.
- `Offramp.Cli` with `System.CommandLine`, global options, exit codes, the
  JSON envelope, NDJSON progress, Spectre rendering, `NO_COLOR`.
- `Offramp.Core`: model records (from `02-workspace-model.md`), `offramp.yml`
  binding + validation (`schemas/v1/config.json`), `DiagnosticBag`,
  `IProgressSink`, `ICache`, `IGitService`.
- Commands: `doctor` (environment checks only), `init` (`--defaults` and
  interactive), `--version`, `--help` with examples.
- Tests: envelope snapshot, config precedence, exit codes, progress protocol,
  `doctor` on a machine without git (mocked).
- CI (`ci.yml`) green on ubuntu/macos/windows; `release.yml` proven with a
  `v0.1.0` tag that publishes to NuGet; CHANGELOG check job.
- `docs/diagnostics.md` generated from the code (a test fails if a code lacks
  an entry).

Acceptance: `dotnet tool install -g offramp --version 0.1.0` works;
`offramp doctor --json` validates against its schema.

## M1 — Workspace ☐

- `scan` from solution, `--binlog`, `--complog`; complog generation; assets
  file parsing; project kind detection; graph with cycles and topological
  order; ledger snapshot; staleness detection; `--fast` investigated (ADR).
- `slice` (`.slnf`).
- `doctor`: Windows-only build steps, CPM hazards, model freshness; `--fix`
  for the compile-only conditional.
- Fixtures: `netfx-only`, `dual-target`, `cycle`, `windows-only-build-steps`
  (with a committed binlog).

Acceptance: `scan` on every fixture produces a snapshot-tested model on all
three OSes; a model produced from a Windows-captured complog of `dual-target`
equals (after scrubbing) the one produced natively.

## M2 — See it ☐

- `graph` (json, dot, mermaid, html with all interactions).
- `deps audit` (feed access, nupkg inspection, cache, Windows-only detection,
  mapping table, statuses); `deps gac`.
- `report` (html, markdown, json) from ledger snapshots.
- Fixtures: `versions`.

Acceptance: HTML graph and report open offline; `deps audit` on `versions`
identifies lowest/newest supporting versions correctly against a recorded feed
(tests use a local file feed so they do not depend on nuget.org).

## M3 — Verify ☐

- `verify` (build mode, command mode, none), baselines, error grouping,
  scratch worktree helper used by consolidation.
- `plan` (order, frontier, `--for`, waves).

Acceptance: `verify` fails on a fixture with an injected error (`OFR5001`) and
passes after removal; command mode merges a JSON envelope from a script.

## M4 — Move tests ☐

- Change sets, journal, rollback, purity enforcement, `git mv`/`File.Move`.
- `move tests` with semantic detection, helper fixpoint, target selection,
  `--create`, path mapping, `InternalsVisibleTo`, `--prune-packages`.
- Fixtures: `tests-in-prod`.

Acceptance: purity test passes (renames only, 100% similarity); helper used
by production code is never moved; `Bar` gets a created `Bar.Tests` that
builds; rollback restores the tree exactly.

## M5 — Move files ☐

- `move plan` (symbol partitioning, co-move closure, trial compilation per
  target, cycle detection, package/project reference proposals, file-pair
  rules, internals), `move apply` (verify policies, resume), `move rollback`.
- `forwarders`.
- Fixtures: `move-cases`.

Acceptance: every case in `move-cases` yields the specified outcome; an
overnight-style run (`--all` on a generated 500-file fixture) completes with a
single verification at the end and a correct plan; `forwarders` output compiles.

## M6 — Dependencies ☐

- `deps consolidate` (constraints, families, pins, CPM output incl. non-default
  path and opt-in, restore verification, preflight hazards).
- `redirects sync`, `deps resolve-dlls`.
- Fixtures: `cpm-shadowing`, `loose-dlls`.

Acceptance: `versions` consolidates to one Newtonsoft version except the
pinned project (`VersionOverride`), the family bump is explained with a
chain, restore verification blocks an induced NU1605; redirects for the web
project are regenerated and stale ones pruned.

## M7 — Audits ☐

- Rule engine and packs; `audit api`, `audit behavior`, `audit serialization`,
  `audit native`; SARIF output; `ifdef report|wrap|strip`.
- Fixtures: `behavior`.

Acceptance: each rule has a positive and a negative test; `audit api` on
`netfx-only` finds the `System.Web` and `System.Drawing` usages with the
correct mapping; serialization classification distinguishes the clone idiom
from file persistence.

## M8 — Dead code and API compat ☐

- `audit dead-code` with confidence levels; `audit api-compat` (ApiCompat
  wrapper).
- Fixtures: `dead-code`.

Acceptance: string-referenced and convention-registered types are never above
`low`; the LOC-removable summary is correct for the fixture.

## M9 — Seams and remote ☐

- `seams` (taint, SCC, min-cut, articulation points, dot/html), `extract
  interface`, `remote` (boundary audit, contracts, host with auto framework
  choice, client, container assets, DI switch, async variant).
- Fixtures: `seams`.

Acceptance: as stated in `commands/seams.md`.

## M10 — Services ☐

- `service` (detection for ServiceBase and Topshelf, worker generation, hosts,
  Dockerfile, k8s, health, removal list).
- Fixtures: `windows-service`.

Acceptance: generated projects build on all OSes; the Linux container image
builds in CI (docker available on ubuntu runner) and responds on the health
endpoint; `--host both` generates the systemd unit and install scripts.

## M11 — Codemods ☐

- `Offramp.Analyzers` with the initial catalog; `codemod list|run`;
  `--format-mode`; analyzer NuGet package.

Acceptance: before/after tests per codemod; idempotency test (second run
changes nothing); fixture-level run verified by build.

## M12 — Web, csproj, config, extract ☐

- `web inventory`, `web scaffold`; `csproj modernize`; `config convert`;
  `move extract`.
- Fixtures: `mvc5`, `legacy-csproj`.

Acceptance: inventory snapshot for `mvc5`; scaffold builds and proxies (YARP
config test); modernize proves identical compile sets via binlog comparison;
config convert round-trips `appSettings` and a custom section.

## M13 — Agents ☐

- `mcp serve`; `Offramp.Llm` adapters; `--llm` gates at the permitted sites;
  architecture test for the layering rule.

Acceptance: an MCP client (test harness) lists tools, runs `offramp_deps_audit`
on a fixture, and receives progress; `--llm` with a stubbed OpenAI-compatible
server names a seam interface and the output marks `source: llm`.

## Later

- Desktop rule pack (WinForms/WPF), VB.NET projects, F# projects.
- `offramp watch` for continuous re-scan in an IDE terminal.
- Corpus expansion (Orchard 1.x, Umbraco 7, ServiceStack v4-era apps).
- Visual Studio / Rider integration via the analyzer package.
