# 0076. Say what a change to the inputs needs, and keep each machine's build state its own

- Status: accepted
- Date: 2026-10-15
- Spec section: `docs/spec/02-workspace-model.md` (staleness), `docs/spec/commands/workspace.md#scan`, `docs/spec/commands/newtonsoft.md#newtonsoft-tests`

## Context

A user migrating a solution on a Mac, and recording .NET Framework probes on a Windows VM, kept
the repository (and with it `.offramp/`, `obj/`, `bin/`) synchronized between the two with a
file-sync tool (issues #55, #56). Several things went wrong without saying why:

- **`OFR0002` right after a scan, for two project files nobody edited** (#55). Both had
  `GenerateSerializationAssemblies` and Windows build steps that `doctor` lists, and `scan` names
  `offramp doctor --fix` as the next step; `doctor --fix --apply` conditions those settings in the
  project files. The model hashed the files before the fix, so every later command called it stale,
  and nothing said the fix was why.
- **`scan` on Windows failed on non-string `.resx` resources** (MSB3822, MSB3823), detected as
  `OFR0119` "Needs Windows to build", on Windows, with `offramp doctor --fix` as the next step,
  which has no fix for it. `dotnet build` cannot embed those resources for .NET Framework without a
  change to the project; Visual Studio's MSBuild (`scan --msbuild`, ADR 0034) can.
- **`scan --msbuild` reported 112 conversion problems with macOS paths** (`OFR0132`, "Missing file
  … /Users/…/.nuget/packages/…"). The compiler calls themselves named the Mac's package folder:
  `obj/project.assets.json` and `obj/*.nuget.g.props` had come from the Mac. Separately, a build
  that writes no binary log, or a conversion that writes no compiler log, left the previous run's
  file in `.offramp/` to be read as this run's.

## Decision

- **A fix that changes the inputs asks for a scan.** When `doctor --fix --apply` writes a file and
  a model exists, it says the model was built before the change, and its next step is
  `offramp scan`. Staleness stays as ADR 0009 has it: content hashes, nothing else.
- **On Windows, a step `dotnet build` cannot run points to MSBuild.** For non-string resources, COM
  references, EDMX, T4, Fakes, inline tasks, MSTest v1, and SSDT, a scan on Windows with the
  `dotnet` builder says the step "needs Visual Studio's MSBuild to build, which `dotnet build` is
  not (`offramp scan --msbuild`)", and the next step is `offramp scan --msbuild`. The steps
  `doctor --fix` conditions for `dotnet build` on Windows (sgen, web targets, `MvcBuildViews`,
  Bcl.Build; ADR 0064), and those Windows needs nothing for (build events, letter case), keep
  `offramp doctor --fix`. After an MSBuild build on Windows there is no next step. `OFR0119`'s and
  `OFR0130`'s fix texts say the same.
- **A run reads only what it wrote.** `scan` deletes `.offramp/msbuild.binlog` before it builds and
  `.offramp/build.complog` before it converts, so a build or conversion that writes nothing fails
  as such (`OFR0130`, no compiler calls) instead of reading another run's or another machine's log.
- **`OFR0132` says where foreign paths come from.** When a missing file is at an absolute path of
  the other kind of OS (`/…` on Windows, `C:\…` elsewhere), the message adds that the build read
  another machine's paths, from `obj/` folders or packages copied or synced from it, and to delete
  the `obj/` folders and scan again.
- **Each machine scans for itself; the repository carries the rest.** The documentation says that
  `.offramp/`'s logs, `bin/`, `obj/`, and `packages/` belong to the machine that built them and
  are not to be synchronized, and describes generating probes on one machine and recording them on
  Windows through the repository (commit, pull, `scan`, `--record --apply`, commit the baselines).

## Alternatives considered

- **Switch to MSBuild by itself when `dotnet build` fails on such a step.** It doubles a build
  that already takes minutes, the result would depend on what happens to be installed, and ADR 0034
  made the builder a choice the user makes; a scan builds once, with one builder, into one log.
- **Make a model from another checkout stale by its `repositoryRoot`.** `workspace.json` is meant
  to be committed (only the logs, cache, journal, and verify folders are ignored), and ADR 0009
  keeps a fresh clone of the same commit up to date wherever it is cloned.
- **Re-hash the files `doctor --fix` writes into the model.** The fix changes what the build does
  (sgen no longer runs under `dotnet build`), so the model really is out of date.
- **Refuse a compiler log recorded elsewhere.** Reading logs from another machine is supported on
  purpose (`--binlog`, `--complog`; ADR 0010); `newtonsoft tests` now matches such a log's
  generated files by their repository-relative path (ADR 0075).

## Consequences

A Windows user is told to use `--msbuild` the first time, instead of applying a fix that changes
nothing for the step that failed. A scan whose build fails before writing a log now stops with
`OFR0130` where it used to go on with an old log. Nothing detects a synchronized `obj/` before the
build fails on it; the `OFR0132` message names it after.
