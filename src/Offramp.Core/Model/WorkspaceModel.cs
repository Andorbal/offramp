using System.Text.Json.Serialization;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;

namespace Offramp.Core.Model;

/// <summary>
/// <c>.offramp/workspace.json</c>, schema v1. Written by <c>scan</c>, read by every
/// other command. See <c>docs/spec/02-workspace-model.md</c>. Every collection is
/// sorted and every path is repository-relative.
/// </summary>
public sealed record WorkspaceModel
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/workspace.json";
    public const int CurrentVersion = 1;

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaUri;

    [JsonPropertyOrder(-1)]
    public int Version { get; init; } = CurrentVersion;

    public required string CreatedAt { get; init; }

    public required string RepositoryRoot { get; init; }

    public string? Solution { get; init; }

    public required WorkspaceSource Source { get; init; }

    public required SdkInfo Sdk { get; init; }

    public IReadOnlyList<ProjectInfo> Projects { get; init; } = [];

    public ProjectGraph Graph { get; init; } = new();

    public SortedDictionary<string, PackageUsage> Packages { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}

[JsonConverter(typeof(CamelCaseEnumConverter<WorkspaceSourceKind>))]
public enum WorkspaceSourceKind
{
    Binlog,
    Complog,
    Build,
}

public sealed record WorkspaceSource(WorkspaceSourceKind Kind, string Path, string Sha256);

public sealed record SdkInfo(string Version, string Os);

[JsonConverter(typeof(CamelCaseEnumConverter<ProjectKind>))]
public enum ProjectKind
{
    Test,
    Web,
    Winforms,
    Wpf,
    Service,
    Console,
    Library,
    Unknown,
}

/// <summary>framework = only net4x; standard = only netstandard; modern = only netN.0 (N ≥ 5, incl. netcoreapp); dual = net4x plus modern or standard.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FrameworkClass>))]
public enum FrameworkClass
{
    Framework,
    Standard,
    Modern,
    Dual,
}

public sealed record ProjectInfo
{
    /// <summary>Repository-relative path; the project's identity.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? AssemblyName { get; init; }

    public string? RootNamespace { get; init; }

    public ProjectKind Kind { get; init; } = ProjectKind.Unknown;

    public string? KindEvidence { get; init; }

    public bool SdkStyle { get; init; }

    public string? Sdk { get; init; }

    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    public FrameworkClass FrameworkClass { get; init; }

    public string? OutputType { get; init; }

    public bool IsTestProject { get; init; }

    public SortedDictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> WindowsOnlyBuildSteps { get; init; } = [];

    public IReadOnlyList<string> Compile { get; init; } = [];

    public bool CompileExplicit { get; init; }

    public IReadOnlyList<string> ProjectReferences { get; init; } = [];

    public IReadOnlyList<PackageReferenceInfo> PackageReferences { get; init; } = [];

    public IReadOnlyList<AssemblyReferenceInfo> AssemblyReferences { get; init; } = [];

    public IReadOnlyList<ComReferenceInfo> ComReferences { get; init; } = [];

    public IReadOnlyList<string> InternalsVisibleTo { get; init; } = [];

    public SortedDictionary<string, ResolvedFramework> Resolved { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, CompilerCallRef> CompilerCalls { get; init; } = new(StringComparer.Ordinal);

    public int Loc { get; init; }

    /// <summary>True when the analysis build failed for this project and parts of the model are missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Partial { get; init; }

    public ProjectConfigState Config { get; init; } = new();
}

public sealed record PackageReferenceInfo
{
    public required string Id { get; init; }

    public string? Version { get; init; }

    public string? VersionOverride { get; init; }

    public string? PrivateAssets { get; init; }

    public IReadOnlyList<string> Tfms { get; init; } = [];
}

[JsonConverter(typeof(CamelCaseEnumConverter<AssemblyReferenceKind>))]
public enum AssemblyReferenceKind
{
    /// <summary>A Framework assembly resolved from the targeting pack or GAC (no HintPath).</summary>
    Framework,

    /// <summary>A file referenced by HintPath.</summary>
    File,
}

public sealed record AssemblyReferenceInfo
{
    public required string Name { get; init; }

    public string? HintPath { get; init; }

    public AssemblyReferenceKind Kind { get; init; }

    public AssemblyFileMetadata? Metadata { get; init; }
}

public sealed record AssemblyFileMetadata
{
    public string? AssemblyVersion { get; init; }

    public string? TargetFramework { get; init; }

    public string? PublicKeyToken { get; init; }
}

public sealed record ComReferenceInfo
{
    public required string Name { get; init; }

    public string? Guid { get; init; }

    public bool EmbedInteropTypes { get; init; }
}

public sealed record ResolvedFramework
{
    public IReadOnlyList<ResolvedPackage> Packages { get; init; } = [];
}

public sealed record ResolvedPackage
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public IReadOnlyList<PackageDependency> Dependencies { get; init; } = [];

    public bool Direct { get; init; }
}

public sealed record PackageDependency(string Id, string Range);

public sealed record CompilerCallRef(string Complog, int Index);

public sealed record ProjectConfigState
{
    public ProjectKind? KindOverride { get; init; }

    public bool Excluded { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Frozen { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<GraphEdgeKind>))]
public enum GraphEdgeKind
{
    /// <summary>A ProjectReference.</summary>
    Project,

    /// <summary>A HintPath reference to another project's output.</summary>
    Assembly,
}

public sealed record GraphEdge(string From, string To, GraphEdgeKind Kind);

public sealed record ProjectGraph
{
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; } = [];

    public IReadOnlyList<string> TopologicalOrder { get; init; } = [];
}

public sealed record PackageUsage
{
    /// <summary>Version → projects using it (both sorted).</summary>
    public SortedDictionary<string, IReadOnlyList<string>> Versions { get; init; } = new(StringComparer.Ordinal);
}
