# `guide`

A walk through the migration for people who have not done one before. The
guide is a checklist of Offramp's own commands in migration order, each with a
plain-language reason, a few simple facts about the repository that decide
which steps apply, and a record of what has been done. It does not plan or
port anything itself, and it is deliberately not clever: when more than one
step could come next, it asks. Decisions in `docs/decisions/0028-guide.md`.

```
offramp guide [--run STEP | --done STEP | --skip STEP | --reset STEP|all] [--project P] [--apply] [--yes]
```

## Modes

- **Session** (a terminal, no `--json`, none of the action flags): shows where
  the migration stands, explains the next step, and asks what to do: run it,
  mark it done, skip it, or stop. When several steps are open it asks which one
  first. It loops until every step is done, skipped, or not needed, or the user
  stops. Progress is saved after every answer.
- **Report** (no terminal, or `--json`, without action flags): the same status
  as data and a human view, and nothing is asked.
- **Actions** (the answers a session asks for, as flags; one per run):
  - `--run STEP`: run the step's command now (see [Writes](#writes)).
  - `--done STEP` / `--skip STEP`: record the step as done or skipped without
    running anything.
  - `--reset STEP`: forget what was recorded for the step; `--reset all`
    forgets everything (and replaces an unreadable progress file).
  - `--project P` names the project for a step done per project (a path or a
    unique name, as elsewhere). Without it, `--done` and `--skip` apply to every
    project of the step, and `--run` needs it unless the step has only one open
    project (`OFR0041`).

**First run.** When `<state>/guide.json` does not exist and no action flag is
given, the guide runs `doctor` before anything else and creates the file with
its outcome. `<state>` is `paths.state` (default `.offramp`).

## Steps

Stages are shown in this order, and the steps within a stage in this order,
which is also the suggested order. `{N}` is the target major version.

| Stage | Step | Command | Writes | Applies when |
|---|---|---|---|---|
| `setup` Get set up | `doctor` | `offramp doctor` | nothing | always |
| | `init` | `offramp init` | config | always |
| | `scan` | `offramp scan` | state | always |
| | `compile-only` | `offramp doctor --fix` | repository | a project has Windows-only build steps |
| `understand` See what you have | `plan` | `offramp plan --waves` | nothing | always |
| | `graph` | `offramp graph` | nothing | always |
| | `deps-audit` | `offramp deps audit` | nothing | the model lists packages |
| | `audit-api` | `offramp audit api` | nothing | always |
| | `audit-behavior` | `offramp audit behavior` | nothing | always |
| | `audit-serialization` | `offramp audit serialization` | nothing | always |
| | `audit-native` | `offramp audit native` | nothing | always |
| | `audit-dead-code` | `offramp audit dead-code` | nothing | always |
| | `web-inventory` | `offramp web inventory --project {project}` | nothing | per project: .NET Framework web projects |
| | `report` | `offramp report` | nothing | always |
| `prepare` Tidy up while still on .NET Framework | `move-tests` | `offramp move tests --project {project} --create` | repository | per project: non-test projects that reference a test framework package |
| | `csproj-modernize` | `offramp csproj modernize --all` | repository | a C# project is not SDK-style |
| | `deps-consolidate` | `offramp deps consolidate --all` | repository | a package is referenced at more than one version |
| | `resolve-dlls` | `offramp deps resolve-dlls` | repository | a project references a DLL file |
| | `codemods` | `offramp codemod run --mod all` | repository | always |
| `port` Port, a wave at a time | `port` | `offramp csproj modernize --project {project} --tfm {tfms}` | repository | per project: projects ready to port (`plan --frontier`) other than web and service projects |
| | `config-convert` | `offramp config convert --project {project}` | repository | per project: .NET Framework applications with an `App.config` or `Web.config` beside the project file |
| | `service` | `offramp service --project {project}` | repository | per project: service projects ready to port |
| | `web-scaffold` | `offramp web scaffold --project {project} --new {new}` | repository | per project: .NET Framework web projects |

- `{tfms}` is the project's target frameworks followed by `net{N}.0`
  (`net{N}.0-windows` for WinForms and WPF projects), quoted in the command
  (`--tfm "net48;net10.0"`). `{new}` is a folder next to the project's folder
  named after the project with `.Core` appended (`src/Shop.Web/Shop.Web.csproj`
  gives `src/Shop.Web.Core`).
- `port` relies on `csproj modernize` building the project for every target in
  a scratch copy: the dry run fails until the code compiles for `net{N}.0`, and
  its errors are the list of what to fix on .NET Framework first, so applying
  never leaves the repository unable to build. The step's `why` says so and
  names `--accept-diff` for fixing forward instead.
- "Writes": `nothing`; `state` (only Offramp's state directory); `config`
  (`offramp.yml` and `.gitignore`, as `init` always does); `repository`
  (project files or code: a dry run unless `--apply`).
- Every step's result carries a `why` paragraph written for someone new to the
  migration; the terminal shows it before asking.

## Status

Each step is `done`, `skipped`, `open`, `blocked`, or `notApplicable`,
decided in this order:

1. **Observed.** Some steps are done because the repository says so, whatever
   was recorded: `init` when `offramp.yml` exists; `scan` while the workspace
   model exists and is fresh; `compile-only` when `Directory.Build.props`
   already has the block; `port` when no project other than web and service
   projects is still .NET Framework-only. When such projects remain but none is
   ready (each waits on a web or service project or sits in a reference cycle),
   `port` is `blocked` with a note instead of `notApplicable`.
2. **Recorded.** A `done` or `skipped` record in the progress file. A step done
   per project takes a project's own record, else the step's record, else it is
   open; the step is done (or skipped, when every project was skipped) once none
   of its projects is open.
3. **Not needed.** Its applies-when fact is false (`notApplicable`). Facts that
   need the workspace model make the step `blocked` while there is none.
4. **Waiting.** A step listed in `requires` is still open or blocked
   (`blocked`): `init` requires `doctor`; `scan` requires `init`;
   `compile-only` requires `doctor` and `scan`; `web-scaffold` requires
   `web-inventory`.
5. Otherwise `open`.

Then stages: a stage is `done` when none of its steps is open or blocked. The
first stage with an open step is `current`; its open steps are `next`, the
choices a session offers. Every other stage is `upcoming`, and its open steps
are reported as `blocked` until it becomes current. A stale workspace model
therefore brings `setup` back as current until the next scan.

`scan` cannot be skipped or marked done (`OFR0043`): every later step reads the
model, and the guide checks the model itself. `doctor` can be marked done when
a failing check does not matter to you.

## Running a step

The guide runs a step's command in process, as if typed, with the global
options `--target`, `--solution`, `--config`, `--workspace`, `--verbose`,
`--no-cache`, `--llm`, and `--no-llm` passed along. With `--json` the command
runs with `--json` too, and its envelope is embedded in `ran[].envelope`;
otherwise it renders to the terminal as usual. The outcome is recorded:

| Exit code | Read-only step | `doctor` | Dry run of a writer | Writer with `--apply` |
|---|---|---|---|---|
| 0 | `done` | `done` | `previewed` | `done` |
| 1 | `done` (findings are the point) | `failed` | `previewed` | `failed` |
| 2, 3, 4, 130 | `failed` | `failed` | `failed` | `failed` |

A `failed` outcome is `OFR0042` (warning) and leaves the step open with a note.
`previewed` also leaves it open, with the command that applies it.

### Writes

The guide never changes the repository on its own. A step that writes to the
repository runs as a dry run unless the guide was started with `--apply`:

- In a session with `--apply`, the dry run comes first; when it exits 0 the
  guide asks whether to apply, and on yes runs the command again with
  `--apply --yes`. `--yes` on the guide answers that question yes.
- With `--run STEP --apply`, the command runs once with `--apply` and confirms
  on a terminal as it always does.

`init` writes `offramp.yml` and `.gitignore` as it always does (it is a dry run
only with `--dry-run`), and `scan` writes only the state directory.

## Progress file

`<state>/guide.json` (`schemas/v1/guide-state.json`), owned by Offramp and
written without `--apply`. It holds no timestamps and no absolute paths, so it
can be committed to share progress with a team.

```jsonc
{
  "$schema": "https://offramp.dev/schemas/v1/guide-state.json",
  "version": 1,
  "records": [                       // sorted by step, then project (null first)
    { "step": "doctor", "project": null, "status": "done", "exitCode": 0 },
    { "step": "graph", "project": null, "status": "skipped", "exitCode": null },
    { "step": "move-tests", "project": "src/Foo/Foo.csproj", "status": "previewed", "exitCode": 0 }
  ]
}
```

`status` is `done`, `skipped`, `previewed`, or `failed`; one record per step
and project. A file that cannot be read is `OFR0040`: the guide stops without
touching it, and `--reset all` replaces it.

## Result

Result (`schemas/v1/guide.json`):

```jsonc
{
  "stateFile": ".offramp/guide.json",
  "started": false,                 // this run created the progress file
  "interactive": false,             // a session ran
  "target": "net10.0",
  "ran": [                          // steps this run executed, in order
    { "step": "doctor", "project": null, "command": "offramp doctor", "exitCode": 0,
      "outcome": "done", "envelope": null }   // the command's envelope with --json
  ],
  "stage": "setup",                 // the current stage; null when nothing is open
  "next": ["init"],                 // the current stage's open steps
  "stages": [
    { "id": "setup", "title": "Get set up", "status": "current",
      "steps": [
        { "id": "init", "title": "Write offramp.yml", "why": "…",
          "command": "offramp init", "writes": "config", "requires": ["doctor"],
          "status": "open", "note": null, "projects": [] }
      ] }
  ],
  "counts": { "steps": 23, "done": 1, "skipped": 0, "open": 1, "blocked": 16, "notApplicable": 5 }
}
```

For a step done per project, `command` shows the `{project}` placeholder and
`projects` lists the candidates:
`{ "project", "status": "done|skipped|open", "command", "note" }`, sorted by
path.

Exit codes follow the conventions. The guide's own findings are its only
diagnostics; a step's diagnostics stay in that step's output or envelope.

Diagnostics: `OFR0040` progress file unreadable (exit 3), `OFR0041` step needs
a project (exit 2), `OFR0042` step did not complete (warning), `OFR0043` step
cannot be skipped or marked done (exit 2), `OFR0021` unknown project (exit 2).
