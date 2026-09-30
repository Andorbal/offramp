# Workspace commands: `scan`, `doctor`, `init`, `plan`, `verify`, `slice`, `report`

## `scan`

Builds the workspace model. See `02-workspace-model.md` for inputs and schema.

```
offramp scan [--solution PATH] [--binlog PATH [--complog PATH] | --complog PATH | --no-build | --msbuild | --msbuild-path PATH] [--if-stale]
```

- Without `--solution` or `solution:`, builds the solution `init` would choose
  (`OFR0023` says which and why; a tie is `OFR0020`, exit 2;
  `docs/decisions/0050-choose-among-several-solutions.md`).
- Runs `dotnet build -bl` unless a log is supplied. Uses `verify.properties`
  and `verify.configuration` from config so the analysis build matches
  verification builds, and `verify.timeoutSeconds` as the build timeout.
  Outside Windows, every `dotnet build` Offramp runs also passes
  `RestorePackages=false` unless `verify.properties` sets it, so a legacy
  `.nuget/NuGet.targets` does not run `NuGet.exe` through Mono.
- `--msbuild` (or `scan.builder: msbuild`) runs MSBuild.exe from Visual Studio
  or the Build Tools instead, for solutions with projects that only .NET
  Framework's MSBuild builds (sgen, COM references, the ASP.NET web application
  targets): `MSBuild.exe <solution> -restore -t:Rebuild -m -bl:<log>
  -p:Configuration=<verify.configuration> ... -p:RestorePackagesConfig=true`,
  then `verify.properties`. That is the build `dotnet build --no-incremental`
  runs, with packages.config projects restored as Visual Studio restores them.
- MSBuild.exe is `--msbuild-path` (which implies `--msbuild`, relative to the
  working directory) or `scan.msbuildPath` (relative to the repository root):
  the file itself, a folder holding it, or an installation folder
  (`MSBuild/Current/Bin`, then `MSBuild/15.0/Bin`). Without either, scan uses
  the Developer Command Prompt's installation (`VSINSTALLDIR`), else the newest
  installation with the MSBuild component that vswhere reports. `PATH` is not
  searched (`docs/decisions/0034-msbuild-for-scan.md`). An MSBuild.exe that is
  not found or cannot be started is `OFR0017` (exit 3). `--msbuild` and
  `--msbuild-path` cannot be combined with `--binlog`, `--complog`, or
  `--no-build` (exit 2).
- Outside Windows, before the build, restores the packages the solution's
  `packages.config` files list into its packages folder (`repositoryPath` from
  `nuget.config`, else `packages/` beside the solution), laid out as
  `nuget restore` lays it out, because `dotnet restore` skips `packages.config`.
  A package comes from the NuGet global packages folder, else from the feeds in
  `nuget.config`. A folder that exists in any letter case is never touched.
  `OFR0106` (info) lists what was written, `OFR0105` each package that could not
  be found (`docs/decisions/0037-legacy-projects-outside-windows.md`).
- Progress: the `packages.config` restore is a phase of its own that reports
  each package (`Id Version`), and the build phase reports each project the
  build finishes (MSBuild's `Name -> output` lines) out of the solution's
  projects, so a long build is never silent.
- When the solution lists ASP.NET Web Site projects (folders without a project
  file, which `dotnet build` cannot build: MSB4249 stops the whole solution),
  `dotnet build` builds `.offramp/scan.slnf`, a filter of every other project,
  instead; the model's `solution` stays the scanned one. Each Web Site is
  `OFR0101` and `OFR0126` (`docs/decisions/0048-web-sites-bcl-build-and-mstest-v1-outside-windows.md`).
- Converts the binlog to a complog (`.offramp/build.complog`) so compilations
  can be rebuilt without MSBuild. With `--complog`, copies that one instead.
- `--no-build` reuses `.offramp/msbuild.binlog` from the previous scan
  (`OFR0003` when there is none); it cannot be combined with `--binlog` or
  `--complog` (exit 2).
- `--if-stale` returns the existing model untouched (`upToDate: true`) when
  it is fresh (`02-workspace-model.md#staleness`).
- Writes `.offramp/workspace.json` and, unless the build failed, a ledger
  snapshot (`ledgerSnapshot` is null then): a partial model would put a false
  step in `report`'s trend.
- There is no `--fast` (`docs/decisions/0008-no-fast-scan.md`).

Result (`schemas/v1/scan.json`):

```jsonc
{
  "model": ".offramp/workspace.json",
  "upToDate": false,
  "source": { "kind": "build", "path": ".offramp/msbuild.binlog", "sha256": null, "complog": null },  // sha256: a supplied log's
  "solution": "src/Monolith.sln",
  "buildSucceeded": true,            // null for a compiler log alone
  "projects": 412, "loc": 1830421,
  "byFrameworkClass": { "dual": 3, "framework": 380, "modern": 12, "standard": 17 },
  "byKind": { "console": 20, "library": 300, "service": 4, "test": 80, "unknown": 1, "web": 7, "winforms": 0, "wpf": 0 },
  "cycles": [ ["src/A/A.csproj", "src/B/B.csproj"] ],
  "windowsOnlyBuildSteps": [ { "project": "src/Soap/Soap.csproj", "steps": ["sgen"] } ],
  "unrecognized": ["src/Odd/Odd.csproj"],
  "notLoaded": [ { "project": "db/Db.sqlproj", "reason": "unsupported project type (.sqlproj)" } ],
  "partial": ["src/Soap/Soap.csproj"],
  "ledgerSnapshot": ".offramp/ledger/2026-09-25-bd7acf72.json"
}
```

Exit codes follow the conventions: a failed analysis build (`OFR0130`, an
error) exits 1 with the model written; a missing log, a build that cannot run
or times out exits 3.

Diagnostics: `OFR0003` no binary log to reuse, `OFR0004` log not found or
unreadable, `OFR0010` no `dotnet`, `OFR0017` no MSBuild.exe (with `--msbuild`),
`OFR0020` several solutions and none chosen, `OFR0023` solution chosen among several, `OFR0022` no
solution, `OFR0101` project not understood (reason, first that applies: an
ASP.NET Web Site project; its evaluation error, or "the restore failed" with
the error; an unsupported project type, named; the failed restore; the
project it references, or the solution makes it depend on
(`ProjectDependencies`), that failed with errors of its own; MSBuild stopped
before evaluating any project, with the first error; the one it references or
depends on that was not built either; else the solution configuration), `OFR0102` kind unknown,
`OFR0103` model from a compiler log alone, `OFR0104` assets file missing,
`OFR0105` packages.config package not restored, `OFR0106` packages.config
packages restored, `OFR0110`–`0119` Windows-only build step detected (one code
per step family; `OFR0118` and `Exec` commands written for cmd.exe are found
from the failed build's errors, `OFR0117` and `OFR0119` also from the
projects' files, all at once, for a log built in this checkout), `OFR0120`
project reference cycle,
`OFR0121` portable target references a framework-only project, `OFR0122` the
compile-only block does not reach a legacy project (outside Windows, from its
evaluation: `MSBuildExtensionsPath` overridden and in which file,
`ImportDirectoryBuildProps=false`, or a nearer `Directory.Build.props`), `OFR0123` a
`Compile` item's file missing in every letter case (naming the git-ignored
ones, which the repository's own build generates), `OFR0124`–`0126`
Windows-only build steps (`Microsoft.Bcl.Build`'s binding redirects, MSTest v1,
an ASP.NET Web Site project), `OFR0130` build failed (errors count
once per project; with the count per error code, most first, and how many
projects each affects in the message and `data.projectsByCode`, an error
without a code labeled `restore` when the restore logged it, and the first N
distinct errors with repository-relative paths; scan still produces a model for projects whose compiler call
succeeded, and marks the rest `partial: true`), `OFR0131` build timed out,
`OFR0132` compiler calls unavailable.

## `doctor`

Checks the environment and the repository, and explains how to fix what's
wrong. Read-only.

```
offramp doctor [--fix [--apply]]
```

Checks, each with pass/warn/fail and a remedy:
- SDKs installed and which one `global.json` selects; whether it can target
  `--target`. When no installed SDK satisfies `global.json`, the remedy names
  the least permissive `rollForward` that selects one (`latestFeature`,
  `latestMinor`, `latestMajor`), or says none is new enough.
- `Microsoft.NETFramework.ReferenceAssemblies.<tfm>` resolvable (offline cache,
  targeting pack, or feed) for every .NET Framework target the projects compile
  for (`net40`, `net461`, ...; `net48` when none is known): the model's targets,
  or before the first scan the solution's project files'. Outside Windows, when
  there are legacy (non-SDK) projects (from the model, or before the first scan
  from the project files), the check warns (`OFR0018`) unless the compile-only
  block has its legacy section, the only way those projects get the package.
- git present; repo detected; `git mv` will be used.
- `offramp.yml` valid; unknown keys; pins without reasons.
- Workspace model present and fresh (`OFR0002` names what changed).
- Windows-only build steps per project (from the model), with the exact
  conditional to add. `verify.properties` overriding `PreBuildEvent` or
  `PostBuildEvent` empties them in the evaluations the model records, so the
  check names the projects whose files still set them: `--fix` shows the diff of the compile-only block against
  the root `Directory.Build.props`; `--fix --apply` writes it (asking first on a
  terminal unless `--yes`), keeping every other byte of the file
  (`docs/decisions/0012-doctor-fix-and-slice.md`).
- CPM shadowing hazards (see `deps.md`), against the model's projects or, before
  the first scan, the solution's. `packages.config` projects (`OFR1303`) count
  only once a `Directory.Packages.props` (or `deps.cpm.file`) exists; before that
  the check passes with "not in use".
- LLM endpoint reachable (only when `llm.enabled`).

Exit 0 when nothing failed; 1 when any check failed.

Result (`schemas/v1/doctor.json`; decided in `docs/decisions/0006-doctor-contract.md`):

```jsonc
{
  "checks": [
    { "id": "dotnet-sdk", "title": ".NET SDK installed", "status": "pass|warn|fail|skip",
      "message": "Installed: 8.0.404, 10.0.100.", "remedy": null, "codes": [] }
  ],
  "environment": {
    "sdks": ["8.0.404", "10.0.100"], "selectedSdk": "10.0.100",
    "globalJson": { "path": "global.json", "version": "10.0.100", "rollForward": "latestFeature" },
    "git": { "version": "2.45.0", "repository": true }, "os": "linux-x64", "target": "net10.0"
  },
  "summary": { "pass": 7, "warn": 1, "fail": 0, "skip": 0 },
  "fix": null   // with --fix: { "file": "Directory.Build.props", "alreadyPresent": false, "applied": false, "diff": "--- a/..." }
}
```

Check ids, in output order: `dotnet-sdk`, `global-json`, `target`,
`reference-assemblies`, `git`, `git-repository`, `config`, `workspace`,
`windows-only-build-steps`, `cpm`; M13 appends `llm`. A check's status
matches its diagnostic's severity (fail = error, warn = warning), so the exit code
follows `--fail-on`. Diagnostics: `OFR0010` no SDK, `OFR0011` global.json SDK not
installed, `OFR0012` SDK cannot target `--target`, `OFR0013` reference assemblies
unresolvable, `OFR0014` git not found, `OFR0015` not a git repository, `OFR0016`
no `offramp.yml` (info), `OFR0018` legacy projects without the legacy section,
`OFR0001` workspace model missing (reported as a warning
by doctor), `OFR0002` model stale, `OFR0110`–`OFR0119` and `OFR0124`–`OFR0126`
Windows-only build steps (from the model), `OFR1301`–`OFR1303` CPM hazards, `OFR1006` feed
unreachable, and the configuration codes `OFR0050`–`OFR0056`.

## `init`

See `03-configuration.md#init`.

## `plan`

Computes a migration order and readiness from the graph. Structural only, fast.

```
offramp plan [--frontier] [--for PROJECT] [--waves] [--exclude-kind test,...]
```

- Default: every project, leaf-first, with its `frameworkClass`, blast radius
  (number of transitive dependents), blockers (the `framework`-only projects it
  depends on, directly or transitively, through the references its portable
  targets use), readiness, and wave.
- Waves: `0` for projects already portable (standard, modern, dual); `1` for
  framework-only projects that can be ported today; `n` for those whose
  framework-only dependencies are all in earlier waves. A standard, modern, or
  dual project is portable (`done`) only when no framework-only project is
  reachable through the references its modern and .NET Standard targets use
  (a dual project's `net4x`-only references do not count, and neither do the
  framework-only projects behind them for the projects referencing it);
  otherwise it is `blocked` by those projects and takes its wave from them like
  a framework-only project. Such a project builds only because the reference
  skips NuGet's compatibility check (a legacy project does), and fails on the
  target at run time; `scan` reports each such reference as `OFR0121`
  (`docs/decisions/0036-portable-projects-behind-framework-only-ones.md`). Members of a reference
  cycle share a wave and are marked `inCycle`; the cycle must be broken first.
  The order is by wave, then blast radius (largest first), then path, so every
  project comes after the framework-only projects it needs
  (`docs/decisions/0017-plan-and-verify.md`).
- `--frontier`: only projects whose dependencies are all `standard`, `modern`,
  or `dual`, i.e. portable today (`readiness: ready`, wave 1).
- `--for PROJECT`: the framework-only projects in `PROJECT`'s closure (itself
  included), in order: the minimal set to port for it to run on the target.
  The closure is the project, the projects it hosts (plugins, areas, and modules
  that land in its folder; `02-workspace-model.md#hosted-projects`), and
  everything they depend on; a project reached through a reference brings its
  dependencies, not the projects its own host loads. `report`'s application
  numbers use the same rule. Naming a hosted project is `OFR0204` (info: it runs
  in its host's application). An unknown project is `OFR0021`.
- `--waves`: groups the human view by wave; the JSON always carries `wave`.
- `--exclude-kind`: leaves projects of those kinds out of the listing; blast
  radius, blockers, and readiness still come from the whole model.
- Result (`schemas/v1/plan.json`): `for`, `frontier`, `excludeKinds`,
  `order: [{ project, name, kind, frameworkClass, wave, blastRadius, blockers: [..], readiness: ready|blocked|done, inCycle }]`,
  `cycles` (those touching a listed project), and
  `counts: { projects, done, ready, blocked, waves }`.

## `verify`

Runs the user's verification. Called standalone or by movers and the
consolidator.

```
offramp verify [--projects P1,P2,...] [--affected-by PATHS] [--all] [--mode build|command|none] [--baseline]
```

- Selection (one of the three options, else `verify.projects.include`, else
  every project; `verify.projects.exclude` always applies last):
  - `--projects`: paths or names; an unknown one is `OFR0021`.
  - `--affected-by PATHS`: the owners of each changed path plus their direct
    dependents. A project file owns itself; a source file belongs to the
    projects compiling it; a `.props`, `.targets`, `global.json`, or
    `nuget.config` file affects every project beneath its folder; any other
    path belongs to the project whose folder holds it, else to every project
    beneath it.
  - `--all`: every project.
  An empty selection verifies nothing (`OFR5090`).
- `mode: build`: one `dotnet build` of the solution when everything is
  selected, else of a solution filter written to `.offramp/verify/verify.slnf`
  (projects without a solution are built one by one), with
  `-c <verify.configuration>`, `-p:` for each of `verify.properties`,
  `--no-restore` unless `verify.restore`, `-warnaserror:<warnAsError>`,
  `-nowarn:<noWarn>`, and a fresh binlog under `.offramp/verify/`. Errors come
  from the binlog, with repository-relative paths; a failed build that logged
  no error contributes one error carrying its last output lines.
- `mode: command`: runs `verify.command` through the shell (`/bin/sh -c`, or
  `cmd.exe /d /s /c` on Windows) in the repository root with environment
  variables `OFFRAMP_VERIFY_PROJECTS` (semicolon list), `OFFRAMP_VERIFY_TARGET`,
  `OFFRAMP_VERIFY_CHANGESET` (path to the change set JSON, when a mover calls
  it). Exit 0 passes. If stdout is a JSON envelope, its diagnostics are merged:
  `OFR` codes as they are, other codes under `OFR5020` with the original code in
  `data.code`; its errors count like build errors. A failed command's last
  output lines are in `outputTail`. `mode: command` without `verify.command` is
  `OFR0053`.
- `mode: none`: records that verification was skipped (`OFR5090`).
- `--baseline` records the current errors in `.offramp/verify/baseline.json`
  (`schemas/v1/verify-baseline.json`; code, project, file, and message, without
  line numbers) and judges the run against it. With a baseline, only errors it
  does not list count: the run passes when every error is known, and new codes
  are `OFR5010`. `.offramp/verify/` is git-ignored by `init`; commit the baseline
  with `git add -f` to share it.
- Timeout: `verify.timeoutSeconds` for the whole run (`OFR5002`).
- Result (`schemas/v1/verify.json`): `mode`, `status`
  (`passed|failed|timedOut|skipped`), `passed`, `scope` (how the projects were
  chosen), per-project `status` (`passed|failed|notVerified`: a project with no
  error of its own in a failed run was not verified), the counted errors grouped
  by code with the count and the first occurrence in path order, `errorCount`,
  `baseline` (known, new, fixed, new codes) or null, `baselineRecorded`,
  `invocations` (command line, binlog, exit code, timed out), and `outputTail`.
  A failed or timed-out run exits 1.

Diagnostics: `OFR5001` verification failed, `OFR5002` timeout, `OFR5010` new
error code compared to baseline, `OFR5020` finding from the verification
command, `OFR5090` verification skipped.

## `slice`

Generates a solution filter for a project closure so a slice of a huge
repository builds quickly.

```
offramp slice --for PROJECT[,PROJECT...] [--include-dependents] [--include-tests] [--format slnf|slngen] [--out slice.slnf]
```

- `PROJECT` is a repository-relative path, a path relative to the working
  directory, or a project name (unique, case-insensitive). An unknown project is
  `OFR0021` (exit 2); a model without a solution is `OFR0022` (exit 3).
- Closure = the projects plus transitive dependencies; `--include-dependents`
  adds transitive dependents (and their dependencies); `--include-tests` adds
  test projects that reference anything in the closure (and their dependencies).
- Output: `.slnf` (solution filter) referencing the original solution, with the
  solution path relative to the filter file and project paths relative to the
  solution, as Visual Studio writes them. `--format slngen` emits a SlnGen
  invocation instead. `--out` writes the filter itself; without it the filter
  goes to stdout. Writing a filter never makes the model stale.
- Other commands accept `--solution slice.slnf`.

Result (`schemas/v1/slice.json`): `for`, `solution`, `projects` (sorted),
`counts: { requested, dependencies, dependents, tests, total }`, `format`,
`output`, and `content` (the filter or command line).

## `report`

The stakeholder-facing progress page and its data.

```
offramp report [--format html|json|markdown] [--out PATH] [--since DATE] [--title TEXT] [--with-graph]
```

- Reads ledger snapshots (`report.ledger`, default `.offramp/ledger`) for the
  series, and the current model for everything else. The series is every
  snapshot of the model's solution at or after `--since` and older than the
  model, then the model itself (snapshots of another solution or filter are
  left out, with `OFR0203`, info); per point: projects and lines of code by framework class and by kind.
  `asOf` is the model's `createdAt` (`docs/decisions/0016-report.md`).
- Areas: projects and lines by framework class per directory holding project
  folders (the `graph --cluster directory` rule).
- Applications (console, service, web, winforms, wpf, except hosted projects):
  the application, the projects it hosts (`hosted`), and everything they depend
  on; status `done` when nothing in that closure is framework-only, `ready` when
  only the application is, `blocked` otherwise; `next` lists the framework-only
  projects in the closure that can be ported today. `plan --for` must agree with
  these numbers. Each hosted web project is `OFR0204` (info), naming its host
  (`docs/decisions/0055-hosted-projects-belong-to-their-host.md`).
- Frontier: framework-only projects whose dependencies are all portable
  (`ready`), most dependents first.
- HTML: single self-contained file with no scripts; sections: headline numbers,
  burn-down of lines of code by framework class over time (stacked, framework at
  the bottom and its edge drawn as the burn-down line), framework class by area
  (stacked bars), application table, the frontier list, and with `--with-graph`
  the dependency graph from `graph --format html` (tests excluded, frontier
  highlighted) in a sandboxed `iframe srcdoc`. Charts are SVG computed by
  Offramp, so the same data gives the same bytes.
- Markdown: the same tables for pasting into an issue or wiki.
- JSON: the data behind every rendering (`schemas/v1/report-data.json`).
- Styling: restrained, light and dark, print-friendly. Framework class colors
  as in `01-cli-conventions.md`. No external fonts, stylesheets, or scripts.
- The format is inferred from `--out`'s extension (`.html`, `.json`, `.md`)
  when omitted; an unknown extension is a usage error. With a format and no
  `--out`, stdout carries exactly the document. Without a format the terminal
  shows the headline, the applications, and the frontier. `--with-graph` needs
  the HTML format.
- `--since DATE`: `yyyy-MM-dd` (the whole day included) or an ISO 8601 time,
  normalized to UTC. `--title`: default `report.title`, else the repository
  folder's name.
- A JSON file in the ledger directory that is not a snapshot is `OFR0202`
  (warning) and left out.

Result (`schemas/v1/report.json`): `{ format, output, report, content }`: the
format (null when none was asked for), the file written, the data above, and the
rendering when it was not written to a file.
