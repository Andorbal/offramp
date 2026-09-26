namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string LlmArea = "llm";

    private const string McpArea = "mcp serve";

    public static readonly DiagnosticDescriptor OFR9001 = new(
        "OFR9001", Severity.Warning,
        "model answer not used",
        "A command asked the configured model for a permitted use (naming, ranking, summarizing, classifying) and the call failed, or the answer was not usable (not a valid name, not one of the candidates). The deterministic value is used instead, so the output is what `--no-llm` gives.",
        "The model's server is down or slow, the API key is missing, or the model ignored the answer format.",
        "Check `llm.url`, `llm.model`, and the key variable (`llm.apiKeyEnv`), or run with `--no-llm`.",
        LlmArea);

    public static readonly DiagnosticDescriptor OFR9101 = new(
        "OFR9101", Severity.Error,
        "path outside the MCP server's root",
        "`offramp mcp serve --root DIR` confines every path a tool call names to DIR, and a call named a path outside it (absolute, or climbing out with `..`). The tool did not run.",
        "An agent passing a path from another checkout, or a relative path meant for a different working directory.",
        "Pass paths inside the root (relative paths are resolved against it), or start the server with a wider `--root`.",
        McpArea);
}
