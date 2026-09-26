# 0027. Let the CLI hand the MCP server its catalog, run tool calls through the CLI in process, and keep every LLM call in the CLI after the deterministic result

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/mcp-and-llm.md`, `docs/spec/00-architecture.md`

## Context

The spec says `Mcp`'s handlers are the CLI's handlers, and also (CLAUDE.md) that `Llm` and
`Mcp` are leaves nothing references and that `Cli` references everything. The architecture
diagram draws `Mcp ──► Cli`, which would make `offramp mcp serve` impossible to host in the
tool without a cycle. It leaves open:
- how tool input schemas are generated, and how arguments become a command line
- what "every tool runs as dry-run" does with `--apply`, and which values `--root` checks
- what progress notifications carry
- the `ILlm.CompleteJsonAsync<T>` signature with source-generated JSON
- where the permitted LLM uses run, what they send, how an answer is validated, and what
  `ranking` ranks when the package map has one entry per key

## Decision

**Direction.** `Cli ──► Mcp`: `Offramp.Mcp` is the protocol server (the SDK's low-level
handlers over stdio) and takes an `McpCatalog` of tools and resources; it references no
Offramp project. `Offramp.Cli` builds the catalog from `OfframpCli.BuildRoot`, the same tree
the parser uses, and each tool call is `OfframpCli.RunAsync([...path, ...arguments,
"--json"])` in process, with the output captured and progress routed through a host-level
sink. So the handlers, options, validation, envelope, and exit codes are the terminal's.

**Schemas and arguments.** Properties are the CLI names without dashes; types come from the
option's value type; `enum` from the option's completions (which is how
`AcceptOnlyFromAmong` is exposed); `required` from the parser. Arrays become repeated
options, or one option with several values where the option allows it. An argument the
command does not have is refused before anything runs.

**Safety.** Without `--allow-apply`, `apply` and `yes` are removed and `--dry-run` is
always added (every writing command already honors it), and a second text block says so
when `apply` was asked for. With `--root`, any string value that is absolute or contains a
`..` segment must resolve inside the root, else `OFR9101` and nothing runs; relative paths
without `..` cannot leave the root, because the calls run with the root as their working
directory. URLs are exempt.

**Progress.** Events are numbered 1, 2, 3, … under one lock (parallel work reports
concurrently, and MCP progress must increase), without a total: Offramp's phases do not
know the whole run's size.

**LLM.** `CompleteJsonAsync` returns a `JsonNode`: model types are source-generated, and
each call site validates the answer against its own rules anyway (a C# interface name, a
candidate's number, symbols it sent). Every use runs in `Offramp.Cli`, after the library
computed the deterministic result, so `Core`, `Workspace`, `NuGet`, `Analysis`, and
`Refactoring` cannot reach a model; an architecture test asserts the project graph and the
compiled references, and proves it catches a violating graph. Answers change only
garnish: names (`naming`), which of several rule entries to show (`ranking`), a summary
paragraph (`summarizing`), and an evidence line (`classifying`, which never changes a
confidence). What the model sees is limited to signatures, names, and numbers, never
method bodies. Values it produced carry `source: llm`, an optional property absent
otherwise so existing outputs are unchanged. For `ranking`, the package map now keeps every
matching entry (exact before prefix, longer prefix first, configuration before the rule
file) instead of overwriting, and the model chooses only when there are several different
replacements.

## Alternatives considered

- `Mcp ──► Cli` with a separate executable for the server: two tools to install, and the
  spec's `offramp mcp serve` would not exist.
- Spawning `offramp` per tool call: the same behavior, slower, and progress would have to
  be parsed back from NDJSON.
- Letting the model re-grade dead-code confidence: a model would then influence what gets
  deleted; the rule is garnish only.
- A generic `CompleteJsonAsync<T>` with reflection-based deserialization: the tool is
  trimmed-safe and source-generated everywhere else.

## Consequences

- New commands appear as tools with no MCP code; their option descriptions are the tool
  documentation.
- A tool call holds the server's stdout only through the protocol; nothing in a command may
  write to `Console` directly (already a rule).
- With the model enabled, outputs can differ between runs; the `source: llm` markers say
  where, and `--no-llm` restores determinism.
