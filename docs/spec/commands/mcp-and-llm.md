# `mcp serve` and the LLM layer

## `mcp serve`

Exposes Offramp to AI agents as a Model Context Protocol server over stdio.

```
offramp mcp serve [--allow-apply] [--root PATH]
```

- One MCP tool per CLI command, named `offramp_<group>_<command>`
  (`offramp_deps_audit`, `offramp_move_plan`). Input schema generated from the
  command's options (same source of truth as the parser). Output is the
  command's JSON envelope. Progress events map to MCP progress notifications.
- Resources: `offramp://workspace` (the model), `offramp://plan/<id>`,
  `offramp://ledger`, `offramp://diagnostics/<code>` (documentation).
- Safety: without `--allow-apply`, every tool runs as dry-run regardless of
  arguments and the response says so. With it, `--apply` is honored but
  commands still never commit.
- `--root` confines all paths to a directory; requests outside it are refused
  (`OFR9101`).
- A `offramp_help` tool returns the command list and the suggested workflow, so
  an agent with no prior knowledge can orient itself.

Implementation: the official `ModelContextProtocol` C# SDK. The handlers are
the same classes the CLI uses (`Offramp.Cli` handlers), so behavior cannot
diverge.

### Details (M13, `docs/decisions/0027-mcp-and-llm.md`)

- `Offramp.Mcp` holds the server (`ModelContextProtocol.Core`, low-level handlers, stdio)
  and knows nothing of Offramp: `Offramp.Cli` builds its catalog from the command tree
  and passes it in, so `Mcp` stays a leaf that only `Cli` references.
- **Tools.** One per command that has an action, except `mcp serve`: the path joined with
  `_`, hyphens as `_` (`offramp_deps_resolve_dlls`). Properties are the command's options
  and arguments by their CLI names without dashes (`from`, `dry-run`), typed from the
  option's value type (boolean, integer, string, arrays of strings), with the accepted
  values of options that restrict them as `enum`, and `required` from the parser. The
  global options a tool accepts: `target`, `solution`, `config`, `out`, `dry-run`,
  `apply`, `yes`, `verbose`, `no-cache`, `llm`, `no-llm`, `fail-on`, `fail-on-stale`.
- **Calls** run `offramp <path> <arguments> --json` in process with the root as the
  working directory. The first text block is the JSON envelope (the second, when there
  is one, a note); `isError` is set for usage and environment failures (exit 2, 3), not
  for findings (exit 1), which the envelope reports.
- **Progress.** Each progress event of the command (phase starts, reports, info logs)
  is one `notifications/progress` for the request's token, numbered 1, 2, 3, … with the
  phase and item as the message.
- **Dry runs.** Without `--allow-apply`, `apply` and `yes` are dropped and `--dry-run` is
  added to every call; when a call asked for `apply`, a second text block says it was
  ignored. Tool descriptions say so too.
- **`--root`** (default: the current directory): a string value that is an absolute path
  or climbs with `..` must resolve inside the root, else the call is refused with
  `OFR9101` (a JSON diagnostic, `isError`) and nothing runs. URLs are not paths.
- **Resources.** `offramp://workspace` (workspace.json), `offramp://ledger` (the
  snapshots' names and the latest one), `offramp://plan/<id>` (`<state>/plans/<id>.json`,
  listed), `offramp://diagnostics/<code>` (Markdown: meaning, causes, fix). The last two
  are also resource templates.
- **`offramp_help`**: the suggested workflow, whether the server applies, and every
  tool with its description.

## LLM layer (`Offramp.Llm`)

```csharp
public interface ILlm
{
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);           // free text
    Task<JsonNode> CompleteJsonAsync(LlmRequest request, CancellationToken ct);     // schema-constrained (request.Schema)
}
```

(The JSON answer is a `JsonNode`: the model types are source-generated, and the call
sites validate the answer themselves; see ADR 0027.)

Adapters:
- `OpenAiCompatibleLlm`: any `/v1/chat/completions` endpoint (LiteLLM,
  OpenRouter, LM Studio, Ollama, vLLM, OpenAI). Model discovery via
  `/v1/models` when `model` is unset.
- `AnthropicLlm`: Messages API, `anthropic-version` header, structured output
  via tool use with a single tool whose schema is the requested JSON schema.

Configuration in `offramp.yml` `llm:` and `OFFRAMP_LLM_*` env vars; provider
selection by `provider:`. Timeouts default to 60 s, retries 2 with backoff,
and every call is logged at `--verbose` with token counts when the provider
reports them.

Permitted uses (`llm.uses`), each an explicit call site with a `--llm` gate:

| Use | Where | Fallback without LLM |
|---|---|---|
| `naming` | `seams` interface/DTO names, `extract interface --name` default | `I` + type name, `<Member>Request/Response` |
| `ranking` | `deps audit` choosing among several mapped replacement packages | first mapping in the rules table |
| `summarizing` | `report --format markdown` executive summary paragraph | template sentence with the numbers |
| `classifying` | `audit dead-code` low-confidence candidates: "is this name likely used by convention?" | left at `low` |

Forbidden uses, enforced by the layering rule: choosing what to move, which
version to select, whether something compiles, or editing code. A code review
finding an `Offramp.Llm` reference in `Core`, `Workspace`, `NuGet`, `Analysis`,
or `Refactoring` is a build break (an architecture test asserts it).

Every LLM-influenced value in an output carries `"source": "llm"` so a reader
can tell.

### Details (M13, `docs/decisions/0027-mcp-and-llm.md`)

- **Gate.** A use asks the model only when `llm.enabled` (or `--llm`) is on, `--no-llm`
  is not given, and the use is in `llm.uses` (default `[naming, ranking]`). Everything
  runs in `Offramp.Cli` after the deterministic result exists; the library projects never
  see the model.
- **Adapters.** Temperature 0. OpenAI-compatible: `POST {url}/chat/completions` with
  `response_format: json_schema` (strict) for JSON answers, `Authorization: Bearer` when a
  key is set, Markdown-fenced JSON tolerated. Anthropic: `POST {url}/messages`
  (`https://api.anthropic.com/v1` when `llm.url` is the default), `x-api-key`, a forced
  `answer` tool whose `input_schema` is the requested schema. Retries on timeouts,
  connection failures, 429, and 5xx; other 4xx fail at once. `--verbose` logs model,
  tokens, and time per call.
- **Fallback.** A failed call, or an answer the call site rejects, is `OFR9001` (a
  warning) and the deterministic value stays; the output is then what `--no-llm` gives.
- **What each use sends and accepts.**
  - `naming` (`seams`, first 10 seams; `extract interface` without `--name`): the type's
    name, its members' signatures, the callers' type names. Accepted: `I` + PascalCase,
    not a keyword, not another seam's name. Marks `seams[].source` / `source`.
  - `ranking` (`deps audit`): when several package-map entries match a package (exact and
    prefix, configuration and rules), their replacement texts; the answer is a number.
    Marks `replacement.source: llm` (otherwise the rule file or `offramp.yml`).
  - `summarizing` (`report --format markdown`): the headline numbers and the largest
    areas' names; plain text up to 2,000 characters. The summary paragraph is always
    there (without the model, a template sentence); the model's is followed by a note in
    the Markdown and marked `summary.source: llm`.
  - `classifying` (`audit dead-code`): up to 50 low-confidence candidates' names, kinds,
    and accessibility, in one request. The answer adds an evidence line and
    `source: llm`; it never changes a confidence or the summary.

