# Offramp: conventions for contributors and agents

This file is the contract for anyone, human or AI, working in this repository.
Read it fully before the first change. The product specification lives in
`docs/spec/`, the ordered work list in `docs/ROADMAP.md`.

## What Offramp is

A .NET global tool (`offramp`) that helps migrate large .NET Framework
codebases to modern .NET incrementally. Deterministic analysis and refactoring
built on Roslyn, MSBuild binary logs, and the NuGet client libraries. See
`README.md` for the pitch and `docs/spec/00-architecture.md` for the shape.

## Non-negotiables

1. **Determinism.** Given the same inputs, a command produces the same output.
   No wall-clock ordering, no dictionary iteration order leaking into output,
   sorted collections everywhere output is produced. Timestamps live only in
   the envelope's `startedAt`/`durationMs`.
2. **Never edit a file as part of a move.** `move` commands rename with
   `git mv` (or a plain move outside git) and touch nothing inside the file.
   A file that would need any edit is excluded and reported. See
   `docs/spec/commands/move.md`.
3. **Verify with the real toolchain.** Compilation verifies moves. `dotnet
   restore` verifies version choices. Do not write a NuGet resolver or a
   type checker.
4. **LLM is optional.** Nothing in `Offramp.Core`, `Offramp.Workspace`,
   `Offramp.NuGet`, `Offramp.Analysis`, or `Offramp.Refactoring` may depend on
   `Offramp.Llm`. Every command works with `--no-llm` and in CI with no network
   beyond NuGet feeds.
5. **Every command has a JSON contract.** Defined in its spec file, emitted
   with `--json`, validated by a snapshot test. Human rendering is a view over
   the same data, never a different computation.
6. **Every diagnostic has a stable code** (`OFR####`), a documented meaning in
   `docs/diagnostics.md`, and a test that produces it.
7. **No destructive operation without `--apply`/`--yes`.** Default is dry run
   for anything that writes to the user's repository. The exception is
   `.offramp/` state, which Offramp owns.
8. **CHANGELOG.md is updated in the same pull request** as the change, under
   `## [Unreleased]`, in Keep a Changelog categories. CI fails without it
   unless the PR carries the `skip-changelog` label.

## Repository layout

```
src/
  Offramp.Cli/            System.CommandLine entry point, Spectre.Console rendering, exit codes
  Offramp.Core/           workspace model types, config (offramp.yml), diagnostics, progress, envelope
  Offramp.Workspace/      binlog/complog ingest, Roslyn compilations, project graph, assets files
  Offramp.NuGet/          feed access, TFM compatibility, audit, consolidation, redirects
  Offramp.Analysis/       audits, test detection, seams, dead code, symbol graphs
  Offramp.Refactoring/    move planner/applier/journal, forwarders, extract, csproj editing
  Offramp.Analyzers/      Roslyn analyzers + code fixes used by `codemod` (netstandard2.0)
  Offramp.Scaffolding/    templates + generators: service, web, remote, config convert, csproj modernize
  Offramp.Reporting/      graph exporters (json/dot/mermaid/html), progress dashboard
  Offramp.Llm/            ILlm + OpenAI-compatible and Anthropic adapters
  Offramp.Mcp/            MCP server exposing the CLI surface
tests/
  Offramp.*.Tests/        one test project per src project (xunit + Verify)
  Offramp.Fixtures/       fixture generator + checked-in fixture solutions (tests/fixtures/)
  Offramp.Corpus.Tests/   opt-in tests against real open-source codebases
docs/
  spec/                   the specification (architecture, conventions, per-command contracts)
  decisions/              ADRs; write one whenever you resolve an ambiguity in the spec
  ROADMAP.md              ordered milestones with acceptance criteria
  RELEASING.md            how a release happens
eng/                      shared MSBuild props, scripts
.github/workflows/        ci.yml, release.yml, corpus.yml
```

Merge small projects together if a split is not pulling its weight, but keep
the dependency direction: `Cli -> everything`, `Llm` and `Mcp` are leaves that
nothing else references, and `Core` references nothing else in `src/`.

## Toolchain

- Language: C# 13, nullable enabled, implicit usings on, warnings as errors.
- Target: `net8.0;net10.0` for the tool (`RollForward=LatestMajor`), `netstandard2.0`
  for `Offramp.Analyzers`. Run on the newest runtime available; MSBuild
  assemblies from a .NET N SDK can only load into a .NET N or newer process.
- `global.json` pins the SDK used to build Offramp itself. Bump deliberately.
- CLI framework: `System.CommandLine`. Terminal rendering: `Spectre.Console`.
- Roslyn: `Microsoft.CodeAnalysis.CSharp.Workspaces`. Binary logs:
  `MSBuild.StructuredLogger` (the reader's current package id) and `Basic.CompilerLog.Util`.
  Project file editing: `Microsoft.Build` (`ProjectRootElement`, no evaluation).
- NuGet: `NuGet.Protocol`, `NuGet.Frameworks`, `NuGet.Versioning`,
  `NuGet.ProjectModel`, `NuGet.Configuration`.
- YAML: `YamlDotNet`. JSON: `System.Text.Json` with source generation for the
  envelope and model types.
- Tests: xunit, `Verify.Xunit` for snapshots, `FluentAssertions` is not used
  (plain xunit asserts keep the dependency surface small).
- Analyzers/fixers tested with `Microsoft.CodeAnalysis.CSharp.CodeFix.Testing`.

## Build and test

```bash
dotnet build                      # whole solution, warnings as errors
dotnet test                       # unit + fixture tests, all platforms
dotnet test --filter Category=Corpus   # opt-in, slow, needs network
dotnet run --project src/Offramp.Cli -- doctor
dotnet pack src/Offramp.Cli -c Release -o artifacts/   # produces the tool package
```

Fixture solutions under `tests/fixtures/` are real, tiny solutions. Tests build
them once per run to obtain binlogs (cached under `tests/.cache/`, ignored by
git). They must build on ubuntu, macos, and windows runners; the SDK pulls
`Microsoft.NETFramework.ReferenceAssemblies` automatically for `net48`
targets. Never add a fixture that needs Windows to build unless it is the
fixture for detecting exactly that (see `windows-only-build-steps`).

## Testing standards

- Every command: at least one snapshot test of `--json` output per fixture it
  applies to, with paths, durations, and versions scrubbed.
- Every diagnostic code: a test that triggers it.
- Every mover: a test proving files are byte-identical after the move and that
  `git status` shows renames only (run in a temp git repo).
- Every "verify" path: a test proving it fails on a deliberately broken input.
  A check that cannot fail is not a check.
- Negative tests for cycle detection, self-reference, pinned packages, and
  disabled rules.

## Coding conventions

- One command = one class implementing `ICommandHandler<TOptions, TResult>` in
  `Offramp.Cli`, delegating all logic to a library project. The CLI project
  contains parsing and rendering only.
- Long operations report through `IProgressSink` (see
  `docs/spec/01-cli-conventions.md`). Never write to `Console` from a library.
- Public model types are records with init-only properties, serialized via
  `System.Text.Json` source generators. Property names are camelCase in JSON.
- Paths in output are repository-relative with forward slashes.
- Errors the user can act on are diagnostics with codes, not exceptions.
  Exceptions are for bugs and environment failures.
- Keep functions short and named for what they decide. Prefer a small
  explicit state machine over clever LINQ when the logic is a migration rule.
- No reflection-based plugin loading in v1. Rule packs are data files
  (`rules/*.yml`) embedded as resources and overridable from `offramp.yml`.

## Git and pull requests

- Branch per milestone or per command for large milestones. Never commit to
  `main` directly.
- Conventional commits (`feat(deps): ...`, `fix(move): ...`, `docs: ...`,
  `test: ...`, `chore: ...`). The scope is the command group.
- A PR is complete when: acceptance criteria in `docs/ROADMAP.md` for its
  milestone are met, `dotnet test` passes on all three OSes, CHANGELOG is
  updated, docs for any changed contract are updated, and a `docs/decisions/`
  entry exists for any spec ambiguity you resolved.
- End commit messages with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`
  when an AI agent wrote them, and PR descriptions with
  `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## When the spec is ambiguous

Choose the option that is more deterministic, more conservative about editing
the user's files, and easier to test. Record the choice as an ADR in
`docs/decisions/NNNN-title.md` using the template there, and move on. Do not
block on questions that a reasonable engineer would decide.

## Things that look like shortcuts but are bugs

- Regex over source code to find symbols, attributes, or usages. Use the
  semantic model.
- Guessing a package's target frameworks from its dependency groups alone.
  Inspect `lib/` and `ref/` in the nupkg.
- Editing a moved file's namespace "to be helpful".
- Committing on the user's behalf. Offramp stages renames and leaves
  everything else in the working tree.
- Silently skipping a project the loader could not understand. Emit
  `OFR0101` with the reason.
- Treating `netstandard2.0` as "portable" without checking for Windows-only
  API references in the assembly.
