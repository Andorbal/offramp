# 00. Architecture

## Purpose

Offramp is one CLI, many commands, one shared model. Commands are thin; the
model and the analysis libraries do the work. This document fixes the shape so
that every command is built the same way.

## The pipeline

```
 user's repo ──► scan ──► .offramp/workspace.json ──► every other command
                  │                ▲
                  │                │ (re-read, never re-derived)
                  ▼                │
      dotnet build -bl  ──►  msbuild.binlog  ──►  compiler log (.complog)
                                   │                      │
                       evaluated props/items      exact Roslyn Compilations
```

1. **`scan`** produces the workspace model (`docs/spec/02-workspace-model.md`).
   It obtains everything from a single MSBuild binary log, either by running
   `dotnet build -bl` itself or by ingesting a binlog/complog the user supplies
   (for example one captured on a Windows agent).
2. **Every other command** reads the model. Commands that need semantics
   (moves, audits, seams, dead code) rehydrate Roslyn `Compilation`s from the
   compiler log through `Basic.CompilerLog.Util`. Commands that only need
   structure (graph, deps, plan) never load Roslyn at all.
3. **Commands that write** do so through `Offramp.Refactoring`'s change set
   abstraction: a list of file renames, project-file edits, and new files,
   which can be rendered as a dry-run diff, applied, journaled, and rolled back.
4. **Verification** (`verify`) is a separate command that other commands call
   according to configuration. It runs the user's real build.

## Why a binary log is the single loading path

- It works with whatever SDK the user has; Offramp never loads MSBuild's
  evaluation engine in-process, so SDK/runtime version mismatches disappear.
- It captures exactly what the compiler saw: references, defines, analyzers,
  generated files. Compilations rebuilt from it match the real build.
- A log captured on Windows can be analyzed on macOS or Linux, because the
  compiler log embeds reference assemblies. This is the escape hatch for
  projects with Windows-only build steps (`docs/compiling-on-macos.md`).
- Structured-log readers give evaluated properties and items per project
  (target frameworks, output type, package references, project references,
  compile items) without a second evaluation.

Costs: the first `scan` builds the solution. Subsequent scans can reuse the
previous binlog (`--no-build`) or a log captured elsewhere (`--binlog`,
`--complog`). Skipping compiler execution
(`-p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true`) was evaluated
in M1 and rejected: dependents lose their compiler calls
(`docs/decisions/0008-no-fast-scan.md`).

Project files are **edited** with `Microsoft.Build.Construction.ProjectRootElement`,
which manipulates XML with full fidelity (whitespace, comments, conditions) and
does not evaluate anything. Where an edit depends on evaluation (which
`ItemGroup` is active for a target framework), the decision is made from the
model, not by evaluating.

## Layering

```
Offramp.Cli ──► Offramp.Reporting ──┐
            ──► Offramp.Scaffolding ─┤
            ──► Offramp.Refactoring ─┼──► Offramp.Analysis ──► Offramp.Workspace ──► Offramp.Core
            ──► Offramp.NuGet ───────┘                                  ▲
            ──► Offramp.Llm  (leaf, optional)                            │
Offramp.Mcp ──► Offramp.Cli's handlers (same code path as the terminal) ──┘
Offramp.Analyzers (netstandard2.0, no Offramp dependencies; consumed by codemod)
```

- `Core`: model records, `offramp.yml` binding, diagnostics, `IProgressSink`,
  the output envelope, path helpers. No Roslyn, no MSBuild, no NuGet.
- `Workspace`: binlog/complog ingest, assets-file parsing (`NuGet.ProjectModel`),
  project graph, project-kind detection, compilation cache.
- `Analysis`: read-only analyses over compilations and the model.
- `Refactoring`: change sets, movers, journal, project-file editing.
- `NuGet`: feeds, TFM compatibility, package inspection cache, consolidation.
- `Scaffolding`: template rendering (Scriban or raw string templates; pick one
  and record the ADR), generators for service/web/remote/config.
- `Reporting`: exporters and the HTML dashboard (single self-contained file,
  inline CSS/JS, no CDN dependency so it opens on locked-down machines).
- `Llm`: `ILlm` with `Complete(prompt, schema?)`; adapters for any
  OpenAI-compatible endpoint (covers LiteLLM, OpenRouter, LM Studio, Ollama)
  and the Anthropic Messages API. Configured in `offramp.yml` or env vars
  `OFFRAMP_LLM_URL`, `OFFRAMP_LLM_MODEL`, `OFFRAMP_LLM_API_KEY`,
  `OFFRAMP_LLM_PROVIDER=openai|anthropic`.
- `Mcp`: exposes each command as an MCP tool whose input schema is generated
  from the command's options and whose output is the command's JSON result.

## Cross-cutting services

| Service | Interface | Notes |
|---|---|---|
| Progress | `IProgressSink` | phases, determinate/indeterminate, per-item messages; Spectre renderer on TTY, NDJSON on stderr with `--json`, silent with `--quiet` |
| Diagnostics | `DiagnosticBag` | `OFR####` codes, severity, location, `data` dictionary; documented in `docs/diagnostics.md` |
| Config | `OfframpConfig` | loaded from `offramp.yml`, then env `OFFRAMP_*`, then CLI flags; last wins; effective config included in every envelope |
| Caching | `ICache` | `.offramp/cache/` for nupkg inspection, feed metadata, compilations; keyed by content hash; `--no-cache` bypasses |
| Git | `IGitService` | detects repo, runs `git mv`, `git status --porcelain`; falls back to `File.Move` outside a repo |
| Change sets | `ChangeSet` | renames, edits, creates; `Preview()` renders a unified diff; `Apply(journal)`; `Rollback(journal)` |
| Verification | `IVerifier` | built-in `dotnet build` verifier or user script; see `commands/workspace.md#verify` |

## Target framework handling

Commands take `--target N` (integer). `N` maps to `netN.0`. Additional
suffixes come from the destination project when relevant (`net10.0-windows`).
Compatibility questions are answered by `NuGet.Frameworks`
(`DefaultCompatibilityProvider.Instance.IsCompatible(target, candidate)`),
never by string comparison. Classes used throughout the model:

| `frameworkClass` | Meaning |
|---|---|
| `framework` | only `net4x` targets |
| `standard` | only `netstandardX.Y` targets |
| `modern` | only `netN.0[-platform]` targets, N ≥ 5 (includes netcoreapp) |
| `dual` | both a `net4x` and a modern or standard target |

## Determinism rules

- Sort every output collection by a stable key (path, then name, then version).
- Never include machine-specific absolute paths; store repository-relative
  paths with `/` separators. The repository root is recorded once in the envelope.
- Version strings come from `NuGet.Versioning` normalization.
- Hash inputs with SHA-256 for cache keys; record the hash in the model so
  staleness is detectable (`OFR0002 workspace model is stale`).

## Performance envelope

Design targets for a 5,000-project, 5-million-line repository:

- `scan` from an existing binlog: under 2 minutes, under 4 GB memory.
- Structural commands (`graph`, `deps audit` from cache, `plan`): seconds.
- `move plan` for 2,000 files: under 30 minutes, dominated by trial compilation.
  Compilations are loaded lazily per project and evicted LRU.
- Anything longer runs with progress and is resumable (`move apply`) or cached
  (`deps audit`).

## Security and privacy

- Offramp never sends source code anywhere unless an LLM provider is
  configured and a command explicitly opts in (`--llm`). Even then, only the
  minimal excerpt needed for the question (a signature, a list of names).
- NuGet feeds are read through the user's `nuget.config`, including private
  feeds and credential providers. No credentials are logged.
- The HTML report contains project names and metrics; it contains no source.
