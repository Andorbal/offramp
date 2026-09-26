using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Offramp.Scaffolding.Csproj;

/// <summary>What the compiler got for one project and target: the inputs <c>csproj modernize</c> must not change.</summary>
public sealed record CompileSet
{
    public required string TargetFramework { get; init; }

    /// <summary>Source files relative to the project's folder, forward slashes, excluding the build's generated files.</summary>
    public required IReadOnlySet<string> Sources { get; init; }

    /// <summary>Referenced assemblies by file name without extension (paths differ between builds and machines).</summary>
    public required IReadOnlySet<string> References { get; init; }

    /// <summary>Each reference's path, by name, for classifying additions.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> ReferencePaths { get; init; } = new Dictionary<string, string>();

    /// <summary>Manifest resource names.</summary>
    public required IReadOnlySet<string> Resources { get; init; }
}

/// <summary>How a converted project's compile set differs from the original's.</summary>
public sealed record CompileSetDifference
{
    public required string TargetFramework { get; init; }

    public IReadOnlyList<string> SourcesAdded { get; init; } = [];

    public IReadOnlyList<string> SourcesRemoved { get; init; } = [];

    public IReadOnlyList<string> ReferencesAdded { get; init; } = [];

    public IReadOnlyList<string> ReferencesRemoved { get; init; } = [];

    /// <summary>References the SDK adds to every .NET Framework project; allowed, reported for information.</summary>
    public IReadOnlyList<string> ImplicitReferencesAdded { get; init; } = [];

    /// <summary>
    /// References from packages that the conversion turned into PackageReference items in a
    /// referenced project: PackageReference flows to dependents, packages.config did not.
    /// Allowed, reported for information.
    /// </summary>
    public IReadOnlyList<string> TransitiveReferencesAdded { get; init; } = [];

    public IReadOnlyList<string> ResourcesAdded { get; init; } = [];

    public IReadOnlyList<string> ResourcesRemoved { get; init; } = [];

    public bool Identical =>
        SourcesAdded.Count == 0 && SourcesRemoved.Count == 0 && ReferencesAdded.Count == 0 && ReferencesRemoved.Count == 0
        && ResourcesAdded.Count == 0 && ResourcesRemoved.Count == 0;
}

/// <summary>Reads compile sets from a binary or compiler log, and compares them.</summary>
public static class CompileSets
{
    /// <summary>The framework assemblies the SDK references implicitly in .NET Framework projects (Microsoft.NET.Sdk's implicit framework references).</summary>
    public static readonly IReadOnlySet<string> SdkImplicitFrameworkReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "System", "System.Core", "System.Data", "System.Drawing", "System.IO.Compression.FileSystem",
        "System.Numerics", "System.Runtime.Serialization", "System.Xml", "System.Xml.Linq",
    };

    /// <summary>
    /// The compile sets, by target framework, of the regular C# compiler calls for a project
    /// file in a log. Sources outside the repository (the temporary target-framework attribute
    /// file) and under <c>obj/</c> are the build's own and are left out.
    /// </summary>
    /// <param name="logPath">A binary log or compiler log.</param>
    /// <param name="projectFile">The project, absolute.</param>
    /// <param name="repositoryRoot">The root sources must be under.</param>
    /// <param name="defaultTargetFramework">The key of a call without a target framework (a legacy project's).</param>
    public static SortedDictionary<string, CompileSet> Read(string logPath, string projectFile, string repositoryRoot, string defaultTargetFramework = "")
    {
        var root = Normalize(repositoryRoot).TrimEnd('/') + "/";
        var result = new SortedDictionary<string, CompileSet>(StringComparer.Ordinal);
        using var reader = CompilerCallReaderUtil.Create(logPath, null, null);
        var project = Normalize(projectFile);
        foreach (var call in reader.ReadAllCompilerCalls())
        {
            if (call.Kind != CompilerCallKind.Regular || !call.IsCSharp || !string.Equals(Normalize(call.ProjectFilePath), project, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(call.ProjectFilePath)!;
            var raw = reader.ReadArguments(call);
            var arguments = CSharpCommandLineParser.Default.Parse(raw, directory, sdkDirectory: null);
            var target = string.IsNullOrEmpty(call.TargetFramework) ? defaultTargetFramework : call.TargetFramework;
            result[target] = new CompileSet
            {
                TargetFramework = target,
                Sources = arguments.SourceFiles
                    .Where(s => Normalize(s.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    .Select(s => Path.GetRelativePath(directory, s.Path).Replace('\\', '/'))
                    .Where(s => !s.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.Ordinal),
                References = arguments.MetadataReferences
                    .Select(r => Path.GetFileNameWithoutExtension(r.Reference.Replace('\\', '/')))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                ReferencePaths = arguments.MetadataReferences
                    .GroupBy(r => Path.GetFileNameWithoutExtension(r.Reference.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Reference.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase),
                Resources = raw.Select(ResourceName).OfType<string>().ToHashSet(StringComparer.Ordinal),
            };
        }

        return result;
    }

    /// <param name="before">The original project's compile set.</param>
    /// <param name="after">The converted project's.</param>
    /// <param name="convertedPackages">Packages the conversion turned into PackageReference items; references from them are transitive additions.</param>
    public static CompileSetDifference Compare(CompileSet before, CompileSet after, IReadOnlyCollection<(string Id, string Version)>? convertedPackages = null)
    {
        var referencesAdded = after.References.Except(before.References, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = (convertedPackages ?? []).Select(p => $"/{p.Id}/{p.Version}/".ToLowerInvariant()).ToList();
        bool Transitive(string name) =>
            after.ReferencePaths.TryGetValue(name, out var path) && folders.Any(f => path.ToLowerInvariant().Contains(f, StringComparison.Ordinal));
        return new CompileSetDifference
        {
            TargetFramework = before.TargetFramework,
            SourcesAdded = Sorted(after.Sources.Except(before.Sources, StringComparer.Ordinal)),
            SourcesRemoved = Sorted(before.Sources.Except(after.Sources, StringComparer.Ordinal)),
            ReferencesAdded = [.. referencesAdded.Where(r => !SdkImplicitFrameworkReferences.Contains(r) && !Transitive(r))],
            ImplicitReferencesAdded = [.. referencesAdded.Where(SdkImplicitFrameworkReferences.Contains)],
            TransitiveReferencesAdded = [.. referencesAdded.Where(r => !SdkImplicitFrameworkReferences.Contains(r) && Transitive(r))],
            ReferencesRemoved = [.. before.References.Except(after.References, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            ResourcesAdded = Sorted(after.Resources.Except(before.Resources, StringComparer.Ordinal)),
            ResourcesRemoved = Sorted(before.Resources.Except(after.Resources, StringComparer.Ordinal)),
        };
    }

    /// <summary>The manifest name of a <c>/resource:file[,name[,access]]</c> argument (the file name when unnamed), else null.</summary>
    private static string? ResourceName(string argument)
    {
        var trimmed = argument.TrimStart('/', '-');
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || trimmed[..colon].ToLowerInvariant() is not ("resource" or "res" or "linkresource" or "linkres"))
        {
            return null;
        }

        var parts = trimmed[(colon + 1)..].Trim('"').Split(',');
        return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : Path.GetFileName(parts[0].Replace('\\', '/'));
    }

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];
}
