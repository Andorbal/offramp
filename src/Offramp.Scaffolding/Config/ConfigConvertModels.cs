namespace Offramp.Scaffolding.Config;

/// <summary>What happened to one section of the configuration file.</summary>
public sealed record ConvertedSection
{
    /// <summary>The element name (<c>appSettings</c>, <c>connectionStrings</c>, <c>billing</c>, <c>system.web</c>).</summary>
    public required string Name { get; init; }

    /// <summary><c>appSettings</c>, <c>connectionStrings</c>, <c>custom</c>, <c>dropped</c>, or <c>unsupported</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The JSON key the section's values are under; empty for appSettings (root keys); null when not converted.</summary>
    public string? JsonKey { get; init; }

    /// <summary>The generated options class, else null.</summary>
    public string? Options { get; init; }

    /// <summary>How many values the JSON holds for it.</summary>
    public int Values { get; init; }

    /// <summary>What was left out or needs attention.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A transform file and the environment file it became.</summary>
public sealed record ConvertedTransform
{
    public required string File { get; init; }

    public required string Environment { get; init; }

    /// <summary>The <c>appsettings.{Environment}.json</c> written, or null when nothing in the transform is an override.</summary>
    public string? Output { get; init; }

    public int Overrides { get; init; }

    /// <summary>Transform steps with no override form (OFR4404).</summary>
    public IReadOnlyList<string> NotConverted { get; init; } = [];
}

/// <summary>The result of <c>offramp config convert</c>.</summary>
public sealed record ConfigConvertResult
{
    public required string Project { get; init; }

    /// <summary>The configuration file read (App.config or Web.config); null when the project has none.</summary>
    public string? Source { get; init; }

    public IReadOnlyList<ConvertedSection> Sections { get; init; } = [];

    public IReadOnlyList<ConvertedTransform> Transforms { get; init; } = [];

    /// <summary>The files written, repository-relative.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>True when <c>--shim</c> wrote ConfigurationManagerShim.</summary>
    public bool Shim { get; init; }

    public IReadOnlyList<string> NextSteps { get; init; } = [];

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}
