# Commands at a glance

Every command takes these options:

| Option | What it does |
|---|---|
| `--json` | the envelope on stdout, progress on stderr |
| `--out FILE` | writes the output to a file |
| `--target N\|TFM` | the target: `10`, `net10.0`, `net10.0-windows`, or `netstandard2.0` |
| `--solution PATH` | which solution to use |
| `--fail-on LEVEL` | the diagnostic level that makes the exit code 1 |

A command that writes to the repository is a dry run unless it gets `--apply`. The full contracts
are in Offramp's `docs/spec/commands/`.

The "Writes" column says what a command may change:
- **state**: only `.offramp/`;
- **config**: `offramp.yml` and `.gitignore`;
- **repo**: project files or code, and only with `--apply`.

The times were measured on a 4-core Linux container, on SmartStoreNET: 25 projects and 387,000
lines of C#. Use them to set timeouts, not as promises.

## Set up and understand

| Command | Writes | Time | Read in the result |
|---|---|---|---|
| `doctor` | nothing | 1 s | `checks[]` whose `status` is not `pass`, with their `remedy` |
| `doctor --fix [--apply]` | repo | 1 s | `fix`: the diffs for the block, project files, and restore files |
| `init --defaults [--solution P]` | config | <1 s | `OFR0020` when there are several solutions |
| `scan [--if-stale]` | state | 50 s (up to several minutes when the build fails) | `buildSucceeded`, `partial[]`, `notLoaded[]`, `windowsOnlyBuildSteps[]`, diagnostics |
| `guide [--run\|--done\|--skip STEP]` | state | 2 s | `stage`, `next`, `stages[].steps[]` with `why` |
| `plan [--waves\|--frontier\|--for P]` | nothing | 1 s | `order[]` (`wave`, `readiness`, `blockers`, `inCycle`), `counts` |
| `graph --format html --out graph.html` | nothing | 1 s | an interactive picture for people; `--highlight frontier` |
| `report --format html --out report.html` | nothing | 1 s | a page for stakeholders, with the trend across scans |
| `slice --for P` | nothing | seconds | a solution filter for building part of the solution |

## Dependencies

| Command | Writes | Time | Read in the result |
|---|---|---|---|
| `deps audit` | nothing | 3 min | each package's verdict (ok, upgrade, replace, blocked) and the evidence |
| `deps consolidate --all\|--package ID` | repo | 5 s | version skew and pins |
| `deps resolve-dlls` | repo | 5 s | checked-in DLL → package proposals; blockers |
| `deps gac` | nothing | 1 min | GAC and framework references and what replaces them |
| `redirects sync [--prune]` | repo | 2 s | changed, stale, and unchanged redirects per application |

## Audits (read-only)

| Command | Time | Read in the result |
|---|---|---|
| `audit api` | 2–4 min | `target`, `summary[]` by rule, `topNamespaces[]`, `findings[]` |
| `audit behavior` | 2 min | findings by rule; decide each rule with the user |
| `audit serialization` | 1 min | persisted (`OFR3203`) vs transient (`OFR3202`) |
| `audit native` | 1 min | P/Invoke and COM interop sites |
| `audit dead-code` | 1.5 min | `summary` (by confidence, removable lines), `projects[].candidates[]` |
| `audit api-compat --project P --baseline REF` | 1 min | public API differences against a release |
| `ifdef report` | seconds | existing `#if` regions by symbol |

## Change code (dry run by default)

| Command | Time | What it does |
|---|---|---|
| `move tests --project P [--create]` | 1 min | moves tests out of a production project, as renames only |
| `move plan --from A --to B --files GLOB\|--all`, then `move apply --plan F` | minutes | moves files in bulk, verified, rolled back on failure |
| `move extract --from P --types T1,T2 --new NAME --tfm ...` | 1–2 min | moves types into a new project |
| `forwarders --from A --to B [--since REF]` | seconds | adds `TypeForwardedTo` for moved types |
| `csproj modernize --all\|--project P [--tfm "net48;net10.0"]` | 2 min for all | converts to SDK-style project files, verified in a scratch copy |
| `codemod list`, then `codemod run --mod NAME` | 1–3 min | makes one mechanical rewrite across the solution |
| `seams --project P` | 1 min | finds the smallest boundary around unportable code |
| `extract interface`, `remote` | seconds | adds an interface around a seam, then an HTTP boundary for it |
| `config convert --project P` | seconds | converts `web.config`/`app.config` to `appsettings.json` |
| `service --project P` | seconds | turns a Windows service into a hosted worker |
| `web inventory --project P` | 30 s | lists controllers, routes, areas, filters, bundles, and modules |
| `web scaffold --project P --new DIR --proxy yarp` | 30 s | sets up an ASP.NET Core project in front of the old site |
| `verify [--projects ...] [--baseline]` | a build | builds with Offramp's verification settings |

## For agents and CI

| Command | What it does |
|---|---|
| `mcp serve [--allow-apply] [--root PATH]` | an MCP server with one tool per command; dry run unless `--allow-apply` |
| `ide check --base origin/main --fail-on error` | fails a pull request that adds .NET Framework-only API |

## `jq` recipes

```bash
# Headline and diagnostics of any envelope
jq '{command: .offramp.command, target: .offramp.target, summary}' out.json
jq -r '.diagnostics | group_by(.code)[] | "\(.[0].code) x\(length) \(.[0].severity): \(.[0].message)"' out.json

# The model: kinds, classes, partial projects, hosted projects
jq -r '.projects[] | [.id, .kind, .frameworkClass] | @tsv' .offramp/workspace.json
jq -r '.projects[] | select(.partial) | .id' .offramp/workspace.json
jq -r '.projects[] | select(.hostedBy) | "\(.id) -> \(.hostedBy.project)"' .offramp/workspace.json

# Plan: waves and what is ready
jq -r '.result.order[] | [.wave, .readiness, .project, (.blockers|length)] | @tsv' plan.json
jq '.result.counts' plan.json

# The guide: where the migration stands
offramp guide --json | jq '{stage: .result.stage, next: .result.next, counts: .result.counts}'
```
