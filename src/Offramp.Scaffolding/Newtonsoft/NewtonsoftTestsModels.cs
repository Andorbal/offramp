using System.Text.Json.Serialization;
using Offramp.Analysis.Newtonsoft;
using Offramp.Core.Json;

namespace Offramp.Scaffolding.Newtonsoft;

[JsonConverter(typeof(CamelCaseEnumConverter<ProbeFramework>))]
public enum ProbeFramework
{
    Xunit,
    Nunit,
    Mstest,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ProbeKind>))]
public enum ProbeKind
{
    /// <summary>Calls the member the scan found for the site.</summary>
    Entry,

    /// <summary>Calls Newtonsoft.Json with the site's settings and payload type.</summary>
    Type,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ProbeInput>))]
public enum ProbeInput
{
    /// <summary>A generated sample, written as C# in the probe.</summary>
    Generated,

    /// <summary>A real payload from <c>--samples</c>, copied to <c>Inputs/</c>.</summary>
    Sample,

    /// <summary>What a serialize probe of the same payload type and settings wrote.</summary>
    Roundtrip,
}

/// <summary>A production project and the test project its probes go into.</summary>
public sealed record ProbeTarget
{
    public required string Production { get; init; }

    public required string TestProject { get; init; }

    public required ProbeFramework Framework { get; init; }

    /// <summary><c>config</c> (newtonsoft.tests.projects), <c>option</c> (--to), <c>name</c>, or <c>reference</c>.</summary>
    public required string By { get; init; }

    /// <summary>The generated folder, repository-relative.</summary>
    public required string Folder { get; init; }
}

public sealed record PlannedProbe
{
    /// <summary><c>&lt;site id&gt;.&lt;entry|type|input&gt;[&lt;k&gt;].&lt;sample&gt;</c>, also the baseline's file name.</summary>
    public required string Id { get; init; }

    public required ProbeKind Kind { get; init; }

    public required SiteDirection Direction { get; init; }

    public required string Site { get; init; }

    public required string TestProject { get; init; }

    /// <summary><c>typical</c>, <c>empty</c>, <c>edge</c>, or <c>sample[-label]</c>.</summary>
    public required string Sample { get; init; }

    public required ProbeInput Input { get; init; }

    /// <summary>For sample and roundtrip inputs: the file (under the folder) or the probe it reads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFrom { get; init; }

    /// <summary>The generated file, repository-relative.</summary>
    public required string File { get; init; }
}

/// <summary>A site no probe exercises, and why.</summary>
public sealed record UncoveredSite(string Site, string Project, string Reason, string Detail);

/// <summary>A file of the generated folder and what happens to it.</summary>
public sealed record GeneratedFile(string Path, string Action);

public sealed record ProbeCoverage
{
    public required int Sites { get; init; }

    public required int WithEntryProbe { get; init; }

    public required int WithTypeProbe { get; init; }

    /// <summary>Uncovered sites by reason.</summary>
    public required IReadOnlyDictionary<string, int> Uncovered { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp newtonsoft tests</c> (<c>schemas/v1/newtonsoft-tests.json</c>).</summary>
public sealed record NewtonsoftTestsResult
{
    public required IReadOnlyList<ProbeTarget> Targets { get; init; }

    public required IReadOnlyList<PlannedProbe> Probes { get; init; }

    public required IReadOnlyList<UncoveredSite> Uncovered { get; init; }

    /// <summary>Generated, edited, deleted, kept (taken over by the user), or unchanged files.</summary>
    public required IReadOnlyList<GeneratedFile> Files { get; init; }

    public required ProbeCoverage Coverage { get; init; }

    /// <summary>The unified diff of the dry run; null once applied.</summary>
    public string? Preview { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    /// <summary><c>generate</c>, <c>record</c>, <c>verify</c>, or <c>remove</c>.</summary>
    public string Mode { get; init; } = "generate";

    /// <summary>The test runs of a recording or a verification.</summary>
    public IReadOnlyList<ProbeRun> Runs { get; init; } = [];

    /// <summary>Each probe's outcome in a recording or a verification.</summary>
    public IReadOnlyList<ProbeOutcome> Outcomes { get; init; } = [];

    /// <summary>The journal of the baselines a recording wrote; null otherwise.</summary>
    public string? BaselineJournal { get; init; }
}
