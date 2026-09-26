namespace Offramp.Workspace.Ingest;

/// <summary>One item from an evaluation: its evaluated include and metadata.</summary>
public sealed record EvaluatedItem(string Include, IReadOnlyDictionary<string, string> Metadata)
{
    public string? Get(string name) => Metadata.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}

/// <summary>
/// What MSBuild evaluated for one project and one target framework, with capture
/// paths still absolute. Produced from a binary log.
/// </summary>
public sealed record EvaluatedProject
{
    /// <summary>Absolute project path as recorded in the log.</summary>
    public required string ProjectFile { get; init; }

    /// <summary>Normalized short target framework ("net48"), or null for the outer multi-targeting evaluation.</summary>
    public string? TargetFramework { get; init; }

    public required IReadOnlyDictionary<string, string> Properties { get; init; }

    /// <summary>Item type → items, only for the item types Offramp reads.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<EvaluatedItem>> Items { get; init; }

    /// <summary>Imported file paths, for build-step detection.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>Targets that ran for this evaluation during the build.</summary>
    public IReadOnlySet<string> TargetsExecuted { get; init; } = new HashSet<string>();

    public string? Property(string name) =>
        Properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public bool IsTrue(string name) => string.Equals(Property(name), "true", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<EvaluatedItem> ItemsOf(string type) =>
        Items.TryGetValue(type, out var items) ? items : [];
}

/// <summary>An MSBuild error recorded in a log.</summary>
public sealed record BuildError(string Code, string Message, string? ProjectFile, string? File, int? Line, int? Column);
