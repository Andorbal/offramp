using System.Text.Json;
using System.Text.Json.Serialization;

namespace Offramp.Fixtures.Feeds;

/// <summary>
/// A recording of real NuGet packages (structure only: file paths, dependency groups,
/// assembly identities and references, deprecation), made by <c>eng/record-feed.cs</c>
/// and replayed offline as a local folder feed or a V3 HTTP feed.
/// </summary>
public sealed record FeedRecording
{
    public required string Source { get; init; }

    public required string RecordedAt { get; init; }

    public required IReadOnlyList<RecordedPackage> Packages { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static FeedRecording Load(string path) =>
        JsonSerializer.Deserialize<FeedRecording>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is not a feed recording.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}

public sealed record RecordedPackage
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public bool Listed { get; init; } = true;

    /// <summary>True for packages written by hand for a test case rather than recorded.</summary>
    public bool Synthetic { get; init; }

    public string Authors { get; init; } = "";

    public string Description { get; init; } = "";

    public RecordedDeprecation? Deprecation { get; init; }

    public IReadOnlyList<RecordedDependencyGroup> DependencyGroups { get; init; } = [];

    public IReadOnlyList<RecordedFile> Files { get; init; } = [];
}

public sealed record RecordedDeprecation(IReadOnlyList<string> Reasons, string? Message, string? AlternateId, string? AlternateRange);

public sealed record RecordedDependencyGroup(string TargetFramework, IReadOnlyList<RecordedDependency> Dependencies);

public sealed record RecordedDependency(string Id, string Range);

/// <summary>A file in the package: an assembly (replayed as a stub with the same identity), text content, or an empty placeholder.</summary>
public sealed record RecordedFile
{
    public required string Path { get; init; }

    public RecordedAssembly? Assembly { get; init; }

    /// <summary>Verbatim content of small text files MSBuild imports (.props, .targets).</summary>
    public string? Content { get; init; }
}

public sealed record RecordedAssembly
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    public string? Culture { get; init; }

    /// <summary>The full public key as hex, or null for an unsigned assembly.</summary>
    public string? PublicKey { get; init; }

    public IReadOnlyList<RecordedAssemblyReference> References { get; init; } = [];

    /// <summary>Values of assembly-level <c>SupportedOSPlatformAttribute</c>s.</summary>
    public IReadOnlyList<string> SupportedOSPlatforms { get; init; } = [];

    /// <summary>The <c>TargetFrameworkAttribute</c> value (<c>.NETFramework,Version=v4.8</c>), or null for none.</summary>
    public string? TargetFramework { get; init; }
}

public sealed record RecordedAssemblyReference(string Name, string Version, string? PublicKeyToken);
