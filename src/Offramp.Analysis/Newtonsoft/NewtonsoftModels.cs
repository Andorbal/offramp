using System.Text.Json.Serialization;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;

namespace Offramp.Analysis.Newtonsoft;

[JsonConverter(typeof(CamelCaseEnumConverter<NewtonsoftReferenceKind>))]
public enum NewtonsoftReferenceKind
{
    /// <summary>A <c>PackageReference</c> with its own version.</summary>
    PackageReference,

    /// <summary>A <c>PackageReference</c> whose version comes from central package management.</summary>
    CentralPackage,

    /// <summary>Listed in the project's <c>packages.config</c>.</summary>
    PackagesConfig,

    /// <summary>A <c>HintPath</c> into a <c>packages/Newtonsoft.Json.&lt;version&gt;/</c> folder the project's packages.config does not list.</summary>
    HintPath,

    /// <summary>A DLL no package provides.</summary>
    LooseDll,

    /// <summary>Through another project's or package's dependency.</summary>
    Transitive,
}

[JsonConverter(typeof(CamelCaseEnumConverter<SiteDirection>))]
public enum SiteDirection
{
    Serialize,
    Deserialize,
    Dom,
}

[JsonConverter(typeof(CamelCaseEnumConverter<SettingsKind>))]
public enum SettingsKind
{
    /// <summary>No settings argument: the defaults (and <c>JsonConvert.DefaultSettings</c>, if assigned).</summary>
    None,

    /// <summary>Created at the call (or in a local initialized once) from values a test can write down.</summary>
    Inline,

    /// <summary>A static field or property.</summary>
    Member,

    /// <summary>Anything a test cannot recreate.</summary>
    Unknown,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ConfigurationKind>))]
public enum ConfigurationKind
{
    Settings,
    Serializer,
    DefaultSettings,
    Converter,
    ContractResolver,
    NamingStrategy,
    Binder,
}

/// <summary>How a project references Newtonsoft.Json, and which release it uses.</summary>
public sealed record NewtonsoftReference
{
    public required string Version { get; init; }

    public required NewtonsoftReferenceKind Via { get; init; }

    /// <summary>The <c>HintPath</c>, repository-relative, when the reference has one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HintPath { get; init; }
}

/// <summary>A binding redirect for Newtonsoft.Json in a project's own configuration file.</summary>
public sealed record NewtonsoftRedirect(string File, string OldVersion, string NewVersion);

public sealed record SiteSettings
{
    public required SettingsKind Kind { get; init; }

    /// <summary>For <see cref="SettingsKind.Member"/>: the member's documentation ID.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Symbol { get; init; }

    /// <summary>Where the settings are created or declared, when that is in the repository.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }
}

/// <summary>A call that serializes, deserializes, or parses JSON.</summary>
public sealed record NewtonsoftSite
{
    /// <summary><c>&lt;containing type&gt;.&lt;member name&gt;#&lt;n&gt;</c>, <c>n</c> counting the type's sites in members of that name in source order.</summary>
    public required string Id { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    /// <summary>The containing member's documentation ID.</summary>
    public required string Member { get; init; }

    /// <summary>The method called, as a documentation ID.</summary>
    public required string Api { get; init; }

    public required SiteDirection Direction { get; init; }

    /// <summary>Fully qualified; empty for DOM sites and when unknown (OFR3720).</summary>
    public required IReadOnlyList<string> PayloadTypes { get; init; }

    public required SiteSettings Settings { get; init; }
}

/// <summary>A place that configures Newtonsoft.Json.</summary>
public sealed record NewtonsoftConfiguration(ConfigurationKind Kind, string File, int Line, string Symbol);

public sealed record NewtonsoftProjectReport
{
    public required string Project { get; init; }

    /// <summary>The target framework whose compilation was read.</summary>
    public required string TargetFramework { get; init; }

    public required NewtonsoftReference Newtonsoft { get; init; }

    public IReadOnlyList<NewtonsoftRedirect> Redirects { get; init; } = [];

    public IReadOnlyList<NewtonsoftSite> Sites { get; init; } = [];

    public IReadOnlyList<NewtonsoftConfiguration> Configuration { get; init; } = [];
}

/// <summary>One place an API or behavior change applies.</summary>
public sealed record NewtonsoftFinding
{
    public required string Rule { get; init; }

    /// <summary>The rule's case, for behavior changes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Case { get; init; }

    public required Severity Severity { get; init; }

    public bool Overridden { get; init; }

    public required string Project { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    /// <summary>The member, type, or construct involved.</summary>
    public required string Symbol { get; init; }

    public required string Message { get; init; }

    public required string Recommendation { get; init; }

    /// <summary>The release-note line(s), for behavior changes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>The behavior's label for the release in use, and for the target.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Current { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    public IReadOnlyDictionary<string, string> Details { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

public sealed record NewtonsoftScanSummary
{
    public required int Projects { get; init; }

    public required int Sites { get; init; }

    /// <summary>Findings by severity (<c>error</c>, <c>warning</c>, <c>info</c>).</summary>
    public required IReadOnlyDictionary<string, int> Findings { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp newtonsoft scan</c> (<c>schemas/v1/newtonsoft-scan.json</c>).</summary>
public sealed record NewtonsoftScanResult
{
    /// <summary>The release the projects are judged against.</summary>
    public required string Target { get; init; }

    public required IReadOnlyList<NewtonsoftProjectReport> Projects { get; init; }

    public required IReadOnlyList<NewtonsoftFinding> Findings { get; init; }

    public required NewtonsoftScanSummary Summary { get; init; }

    /// <summary>Projects that reference Newtonsoft.Json but could not be read, with why.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}
