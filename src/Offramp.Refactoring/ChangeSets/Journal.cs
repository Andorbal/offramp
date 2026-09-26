using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Refactoring.ChangeSets;

[JsonConverter(typeof(CamelCaseEnumConverter<JournalStepKind>))]
public enum JournalStepKind
{
    Create,
    Edit,
    Rename,
}

[JsonConverter(typeof(CamelCaseEnumConverter<JournalState>))]
public enum JournalState
{
    /// <summary>Steps are being performed; an interrupted run stays in this state.</summary>
    Applying,

    Applied,

    RolledBack,
}

/// <summary>One step, written before it is performed and marked done after.</summary>
public sealed record JournalStep
{
    public required JournalStepKind Kind { get; init; }

    /// <summary>The file created or edited, or the rename's destination. Repository-relative.</summary>
    public required string Path { get; init; }

    /// <summary>The rename's source, else null.</summary>
    public string? From { get; init; }

    /// <summary>The edited file's original bytes (base64), so rollback restores them exactly; else null.</summary>
    public string? Before { get; init; }

    /// <summary>What a created or edited file contains after the step (base64), so an interrupted run can be finished; else null.</summary>
    public string? After { get; init; }

    /// <summary>The SHA-256 the file at <see cref="Path"/> has after the step (a renamed file's never changes); rollback checks it.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Directories this step created (deepest first), removed again by rollback when empty.</summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];

    public bool Done { get; init; }
}

/// <summary><c>.offramp/journal/&lt;time&gt;-&lt;command&gt;.json</c> (<c>schemas/v1/journal.json</c>).</summary>
public sealed record Journal
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/journal.json";

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaUri;

    [JsonPropertyOrder(-1)]
    public int Version { get; init; } = 1;

    /// <summary>The command that wrote the journal, for example <c>move tests</c>.</summary>
    public required string Command { get; init; }

    public required string CreatedAt { get; init; }

    /// <summary>The plan file applied (<c>move apply</c>), repository-relative; else null.</summary>
    public string? Plan { get; init; }

    public required JournalState State { get; init; }

    public required IReadOnlyList<JournalStep> Steps { get; init; }
}
