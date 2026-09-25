using System.Text.Json.Nodes;
using Offramp.Core.Json;

namespace Offramp.Fixtures;

/// <summary>
/// Snapshot scrubbers (<c>docs/spec/04-testing-and-fixtures.md#snapshot-hygiene</c>):
/// repository root, timestamps, durations, versions, and hashes. Ordering is
/// never scrubbed; an ordering change is a determinism bug.
/// </summary>
public static class Scrub
{
    public const string RootPlaceholder = "{RepositoryRoot}";

    public static string Envelope(string json, string? repositoryRoot = null)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        if (node["offramp"] is JsonObject header)
        {
            repositoryRoot ??= header["repositoryRoot"]?.GetValue<string>();
            header["version"] = "{Version}";
            header["startedAt"] = "{StartedAt}";
            header["durationMs"] = 0;
            header["repositoryRoot"] = RootPlaceholder;
            if (header["workspaceHash"] is JsonValue)
            {
                header["workspaceHash"] = "sha256:{Hash}";
            }
        }

        var text = OfframpJson.Format(node);
        return repositoryRoot is null ? text : ReplaceRoot(text, repositoryRoot);
    }

    /// <summary>Replaces the repository root (either slash style, JSON-escaped or not) with a placeholder.</summary>
    public static string ReplaceRoot(string text, string repositoryRoot)
    {
        var forward = repositoryRoot.Replace('\\', '/');
        var escaped = repositoryRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        return text
            .Replace(escaped, RootPlaceholder, StringComparison.Ordinal)
            .Replace(repositoryRoot, RootPlaceholder, StringComparison.Ordinal)
            .Replace(forward, RootPlaceholder, StringComparison.Ordinal);
    }

    /// <summary>Normalizes line endings and trailing spaces in human-readable output.</summary>
    public static string Text(string text, string? repositoryRoot = null)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
        var joined = string.Join('\n', lines);
        return repositoryRoot is null ? joined : ReplaceRoot(joined, repositoryRoot);
    }
}
