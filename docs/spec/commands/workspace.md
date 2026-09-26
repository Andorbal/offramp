# Workspace commands: `scan`, `doctor`, `init`, `plan`, `verify`, `slice`, `report`

## `scan`

Builds the workspace model. See `02-workspace-model.md` for inputs and schema.

```
offramp scan [--solution PATH] [--binlog PATH [--complog PATH] | --complog PATH | --no-build] [--if-stale]
```

- Runs `dotnet build -bl` unless a log is supplied. Uses `verify.properties`
  and `verify.configuration` from config so the analysis build matches
  verification builds, and `verify.timeoutSeconds` as the build timeout.
- Converts the binlog to a complog (`.offramp/build.complog`) so compilations
  can be rebuilt without MSBuild. With `--complog`, copies that one instead.
- `--no-build` reuses `.offramp/msbuild.binlog` from the previous scan
  (`OFR0003` when there is none); it cannot be combined with `--binlog` or
  `--complog` (exit 2).
- `--if-stale` returns the existing model untouched (`upToDate: true`) when
  it is fresh (`02-workspace-model.md#staleness`).
- Writes `.offramp/workspace.json` and a ledger snapshot.
- There is no `--fast` (`docs/decisions/0008-no-fast-scan.md`).

Result (`schemas/v1/scan.json`):

```jsonc
{
  "model": ".offramp/workspace.json",
  "upToDate": false,
  "source": { "kind": "build", "path": ".offramp/msbuild.binlog", "sha256": "...", "complog": null },
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
unreadable, `OFR0010` no `dotnet`, `OFR0020` several solutions, `OFR0022` no
solution, `OFR0101` project not understood (reason), `OFR0102` kind unknown,
`OFR0103` model from a compiler log alone, `OFR0104` assets file missing,
`OFR0110`–`0115` Windows-only build step detected (one code per step family),
`OFR0120` project reference cycle, `OFR0130` build failed (with the first N
errors; scan still produces a model for projects whose compiler call
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
  `--target`.
- `Microsoft.NETFramework.ReferenceAssemblies` resolvable (offline cache or feed).
- git present; repo detected; `git mv` will be used.
- `offramp.yml` valid; unknown keys; pins without reasons.
- Workspace model present and fresh (`OFR0002` names what changed).
- Windows-only build steps per project (from the model), with the exact
  conditional to add: `--fix` shows the diff of the compile-only block against
  the root `Directory.Build.props`; `--fix --apply` writes it (asking first on a
  terminal unless `--yes`), keeping every other byte of the file
  (`docs/decisions/0012-doctor-fix-and-slice.md`).
- CPM shadowing hazards (see `deps.md`), against the model's projects or, before
  the first scan, the solution's.
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
no `offramp.yml` (info), `OFR0001` workspace model missing (reported as a warning
by doctor), `OFR0002` model stale, `OFR0110`–`OFR0115` Windows-only build
steps (from the model), `OFR1301`–`OFR1303` CPM hazards, `OFR1006` feed
unreachable, and the configuration codes `OFR0050`–`OFR0056`.

## `init`

See `03-configuration.md#init`.

## `plan`

Computes a migration order and readiness from the graph. Structural only, fast.

```
offramp plan [--frontier] [--for PROJECT] [--waves] [--exclude-kind test]
```

- Default: topological order, leaf-first, with each project's
  `frameworkClass`, blast radius (number of transitive dependents), and
  blockers (dependencies that are `framework`-only).
- `--frontier`: only projects whose dependencies are all `standard`, `modern`,
  or `dual`, i.e. portable today.
- `--for PROJECT`: the minimal closure that must be ported for `PROJECT` to
  run on the target, in order.
- `--waves`: groups the order into waves where every project in a wave depends
  only on earlier waves.
- Result JSON: `order: [{ project, wave, frameworkClass, blastRadius, blockers: [..], readiness: ready|blocked|done }]`,
  `cycles`.

## `verify`

Runs the user's verification. Called standalone or by movers and the
consolidator.

```
offramp verify [--projects P1,P2,...] [--affected-by PATHS] [--all] [--mode build|command|none]
```

- `mode: build`: `dotnet build` of the given projects (or the projects
  affected by a change set plus their direct dependents) with
  `verify.properties`, `--no-restore` unless `verify.restore`,
  `-warnaserror:<warnAsError>`, `-nowarn:<noWarn>`, `-p:TreatWarningsAsErrors`
  as configured, and a fresh binlog under `.offramp/verify/`.
- `mode: command`: runs `verify.command` with environment variables
  `OFFRAMP_VERIFY_PROJECTS` (semicolon list), `OFFRAMP_VERIFY_TARGET`,
  `OFFRAMP_VERIFY_CHANGESET` (path to the change set JSON). Exit 0 passes. If
  the command prints a JSON envelope, its diagnostics are merged.
- `mode: none`: records that verification was skipped (`OFR5090`).
- Result: `passed`, per-project status, new errors grouped by code with the
  first occurrence of each, and a diff against the baseline error set when a
  baseline exists (`verify --baseline` records one).

Diagnostics: `OFR5001` build failed, `OFR5002` timeout, `OFR5010` new error
code compared to baseline, `OFR5090` verification skipped by config.

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
offramp report [--format html|json|markdown] [--out PATH] [--since DATE] [--title TEXT]
```

- Reads ledger snapshots. Computes per snapshot: projects and lines of code by
  framework class, by kind, by top-level directory; applications (console,
  service, web) and their readiness from `plan --for`.
- HTML: single self-contained file; sections: headline numbers, burn-down of
  `framework`-class lines of code over time, framework class by area (stacked
  bars), application readiness table, the frontier list, and the dependency
  graph from `graph --format html` embedded when `--with-graph`.
- Markdown: the same tables for pasting into an issue or wiki.
- JSON: the series used by the charts.
- Styling: restrained, light and dark, print-friendly. Framework class colors
  as in `01-cli-conventions.md`. No external fonts or scripts.
