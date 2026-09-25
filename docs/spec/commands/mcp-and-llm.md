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

## LLM layer (`Offramp.Llm`)

```csharp
public interface ILlm
{
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);           // free text
    Task<T> CompleteJsonAsync<T>(LlmRequest request, CancellationToken ct);         // schema-constrained
}
```

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
