using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Offramp.Core.Diagnostics;

/// <summary>One finding, as it appears in the envelope's <c>diagnostics</c> array.</summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }

    public required Severity Severity { get; init; }

    public required string Message { get; init; }

    /// <summary>Repository-relative project path, or null.</summary>
    public string? Project { get; init; }

    /// <summary>Repository-relative file path, or null.</summary>
    public string? File { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary>Structured details. Keys are sorted; values are JSON.</summary>
    public SortedDictionary<string, JsonNode?> Data { get; init; } = new(StringComparer.Ordinal);

    public required string Help { get; init; }

    /// <summary>True when <c>offramp.yml</c> changed this diagnostic's severity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Overridden { get; init; }
}

/// <summary>Where a diagnostic points. All paths are repository-relative with forward slashes.</summary>
public sealed record DiagnosticLocation(string? Project = null, string? File = null, int? Line = null, int? Column = null);

/// <summary>Counts by severity, as in the envelope's <c>summary</c>.</summary>
public sealed record DiagnosticSummary(int Errors, int Warnings, int Info);
