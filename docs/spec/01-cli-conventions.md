# 01. CLI conventions

Every command follows these rules. A command that deviates is a bug.

## Invocation

```
offramp <group> <command> [options]
offramp <command> [options]          # top-level commands: scan, doctor, init, graph, plan, verify, slice, report
```

Groups: `deps`, `move`, `audit`, `extract`, `csproj`, `config`, `codemod`,
`mcp`. Single-word commands: `scan`, `doctor`, `init`, `graph`, `plan`,
`verify`, `slice`, `report`, `seams`, `remote`, `service`, `web`,
`forwarders`, `ifdef`, `redirects`.

## Global options

| Option | Default | Meaning |
|---|---|---|
| `--target N` / `-t N` | `config.target` (10) | integer major version of the modern target |
| `--solution PATH` / `-s` | auto-detected | the `.sln`/`.slnx`/`.slnf` to work on; required when more than one exists |
| `--workspace PATH` | `.offramp/workspace.json` | model to read; commands fail with `OFR0001` if missing and suggest `offramp scan` |
| `--config PATH` | `offramp.yml` at repo root | config file |
| `--json` | off | JSON envelope on stdout, NDJSON progress on stderr, no colors |
| `--out PATH` / `-o` | stdout | write the primary output to a file (JSON, HTML, DOT, plan files); without `--json` the human view still goes to stdout and the file receives the envelope |
| `--dry-run` | on for writers | show what would change; writers require `--apply` to change the repo |
| `--apply` | off | perform changes |
| `--yes` / `-y` | off | skip interactive confirmations (implied when not a TTY) |
| `--quiet` / `-q` | off | no progress, only the result and errors |
| `--verbose` / `-v` | off | debug detail on stderr |
| `--no-cache` | off | ignore `.offramp/cache/` |
| `--no-llm` | on unless `config.llm.enabled` | never call an LLM |
| `--llm` | off | allow LLM garnish for this run |
| `--fail-on LEVEL` | `error` | exit non-zero if any diagnostic at or above `LEVEL` (`info`, `warning`, `error`, `never`) |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success, no diagnostics at or above `--fail-on` |
| 1 | completed, findings at or above `--fail-on` (audit findings, verify failure, unsatisfiable consolidation) |
| 2 | usage error (bad options, missing required argument) |
| 3 | environment error (no SDK, no workspace model, binlog unreadable, git unavailable when required) |
| 4 | partial: some operations applied, some skipped; details in the envelope |
| 130 | interrupted (Ctrl-C); journaled commands are resumable |

When several apply, the first in this order wins: 2, 3, 4, 1, 0. An invalid
`offramp.yml` is a usage error (2) for every command except `doctor` and `init`,
which report it. An unexpected exception is an internal error: diagnostic
`OFR0099`, exit 3. See `docs/decisions/0002-cli-foundations.md`.

## Output envelope (`--json`)

```json
{
  "$schema": "https://offramp.dev/schemas/v1/envelope.json",
  "offramp": {
    "version": "0.4.0",
    "command": "deps audit",
    "target": "net10.0",
    "repositoryRoot": "/abs/path",      // the only absolute path in the document
    "solution": "src/Monolith.sln",
    "workspaceHash": "sha256:...",
    "startedAt": "2026-09-25T20:11:04Z",
    "durationMs": 18234,
    "effectiveConfig": { ... }          // the merged config that produced this result
  },
  "result": { ... },                    // command-specific, schema per command spec
  "diagnostics": [
    {
      "code": "OFR1203",
      "severity": "warning",
      "message": "Newtonsoft.Json 9.0.1 is pinned in Customer.Api; 13.0.3 would otherwise be selected",
      "project": "src/Customer.Api/Customer.Api.csproj",
      "file": null, "line": null, "column": null,
      "data": { "package": "Newtonsoft.Json", "pinned": "9.0.1", "selected": "13.0.3", "reason": "..." },
      "help": "https://offramp.dev/diagnostics/OFR1203"
    }
  ],
  "summary": { "errors": 0, "warnings": 3, "info": 12 }
}
```

- `result` schemas live under `schemas/v1/<command>.json` in the repo and are
  validated in tests.
- Adding a field is not a breaking change. Renaming, removing, or changing the
  meaning of one is, and is called out in CHANGELOG.
- Without `--json`, the same object drives Spectre rendering. There is no
  second code path.

## Progress protocol (stderr)

With `--json`, progress is NDJSON on stderr, one object per line:

```json
{"event":"phase","name":"Loading compilations","index":2,"of":5}
{"event":"progress","phase":"Loading compilations","current":143,"total":612,"item":"src/Foo/Foo.csproj"}
{"event":"log","level":"info","message":"Reusing cached nupkg metadata for 1,204 packages"}
{"event":"done","phase":"Loading compilations","durationMs":40211}
```

On a TTY without `--json`, the same events render as a Spectre status/progress
display: phase list with checkmarks, a bar per determinate phase, the current
item, elapsed time, and an ETA once the rate is stable. When stderr is not a
TTY and `--json` is not set, progress degrades to one plain line per phase.

Rendering rules:
- Respect `NO_COLOR` and `TERM=dumb`.
- Never redraw more than 10 times per second.
- Never print progress to stdout.
- Final summary: a compact panel with counts and the next suggested command
  (for example after `scan`: "Try `offramp graph --format html`").

## Diagnostics

Code ranges:

| Range | Area |
|---|---|
| OFR0001–0099 | workspace/model (missing, stale, unreadable) |
| OFR0100–0199 | project loading (unrecognized project, Windows-only build step) |
| OFR1000–1999 | dependencies (`deps *`, `redirects`) |
| OFR2000–2999 | moves (`move *`, `forwarders`, `extract`) |
| OFR3000–3999 | audits (`audit *`, `ifdef`) with sub-ranges per audit |
| OFR4000–4999 | scaffolding (`service`, `web`, `remote`, `seams`, `csproj`, `config`, `codemod`) |
| OFR5000–5999 | verification |
| OFR9000–9999 | LLM and MCP |

Every code has an entry in `docs/diagnostics.md` with meaning, typical cause,
and the fix. Severity may be overridden in `offramp.yml` (`rules:`), and an
overridden diagnostic carries `"overridden": true` in its JSON so suppression
is visible.

## Human-facing UX

Offramp should feel like a well-made modern CLI (think OpenCode, Claude Code,
`gh`): calm, fast to read, never noisy.

- **Headline first.** The first line after a command finishes states the
  outcome in one sentence.
- **Tables for lists, panels for summaries, trees for hierarchies.** No ASCII
  art beyond that.
- **Color carries meaning, not decoration.** Red = blocking, yellow = needs a
  decision, green = ready, dim = informational. Framework classes have fixed
  colors used everywhere including the HTML report: framework = orange,
  standard = blue, modern = green, dual = teal.
- **Dry runs render a unified diff** with file headers, colored, paged through
  `$PAGER` only if the output exceeds the terminal height and stdout is a TTY.
- **Confirmations** appear only for `--apply` on a TTY without `--yes`, and
  state the counts: "Move 412 files across 9 projects and edit 6 project
  files? [y/N]".
- **Suggest the next step** in the summary when there is an obvious one.
- **Errors say what to do.** Every environment error names the missing piece
  and the command that fixes it.

## Interactivity

No command *requires* a TTY. Anything a prompt can ask has a flag. The `init`
command is the only conversational one, and `init --defaults` writes the
detected configuration without asking.
