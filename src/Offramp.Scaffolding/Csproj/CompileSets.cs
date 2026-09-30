using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Common;
using NuGet.ProjectModel;

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
    /// Assemblies of packages the converted project's restore resolved that the legacy project did
    /// not reference: packages flow to dependents with PackageReference, and a package brings all of
    /// its assemblies, where packages.config did neither. Allowed, reported with the package.
    /// </summary>
    public IReadOnlyList<string> TransitiveReferencesAdded { get; init; } = [];

    /// <summary>
    /// .NET Framework assemblies that restored packages declare (their nuspec's
    /// <c>frameworkAssemblies</c>), which PackageReference adds for every package in the graph.
    /// Allowed, reported with the packages.
    /// </summary>
    public IReadOnlyList<string> FrameworkReferencesAdded { get; init; } = [];

    /// <summary>
    /// .NET Standard facades the legacy build added from the framework's <c>Facades</c> folder or
    /// <c>Microsoft.NET.Build.Extensions</c> (<c>ImplicitlyExpandDesignTimeFacades</c>) and the SDK does
    /// not. They only forward types, so a converted build that succeeds does not need them. Allowed.
    /// </summary>
    public IReadOnlyList<string> FacadesRemoved { get; init; } = [];

    /// <summary>The evidence for the allowed changes above, by kind and source.</summary>
    public IReadOnlyList<ReferenceExplanation> Explanations { get; init; } = [];

    public IReadOnlyList<string> ResourcesAdded { get; init; } = [];

    public IReadOnlyList<string> ResourcesRemoved { get; init; } = [];

    public bool Identical =>
        SourcesAdded.Count == 0 && SourcesRemoved.Count == 0 && ReferencesAdded.Count == 0 && ReferencesRemoved.Count == 0
        && ResourcesAdded.Count == 0 && ResourcesRemoved.Count == 0;
}

/// <summary>
/// References that changed for a reason outside the project: <c>package</c> (the assembly is one of a
/// restored package's compile assemblies), <c>frameworkAssemblies</c> (restored packages declare it), or
/// <c>facades</c> (a .NET Standard facade the legacy build added).
/// </summary>
/// <param name="Change"><c>added</c> or <c>removed</c>.</param>
/// <param name="Reason"><c>package</c>, <c>frameworkAssemblies</c>, or <c>facades</c>.</param>
/// <param name="Source">The package (<c>Id Version</c>), the packages that declare the assembly, or the facades' folder.</param>
/// <param name="References">The references, sorted.</param>
public sealed record ReferenceExplanation(string Change, string Reason, string Source, IReadOnlyList<string> References);

/// <summary>What restore resolved for a converted project and target (its <c>project.assets.json</c>).</summary>
public sealed record RestoredPackages
{
    public static readonly RestoredPackages None = new();

    /// <summary>
    /// Each package's compile assemblies (<c>/id/version/lib/net45/X.dll</c>, lower case) and the package
    /// (<c>Id Version</c>). Only these: a package's <c>build/</c> folder can hold the framework's own
    /// reference assemblies (Microsoft.NETFramework.ReferenceAssemblies), which are not the package's.
    /// </summary>
    public IReadOnlyDictionary<string, string> Assemblies { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Framework assembly → the packages (<c>Id Version</c>, sorted) that declare it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FrameworkAssemblies { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The packages of a target framework in an assets file; <see cref="None"/> when it is missing or has no such target.</summary>
    public static RestoredPackages Read(string assetsFile, string targetFramework)
    {
        var lockFile = File.Exists(assetsFile) ? LockFileUtilities.GetLockFile(assetsFile, NullLogger.Instance) : null;
        var targets = lockFile?.Targets.Where(t => string.IsNullOrEmpty(t.RuntimeIdentifier)).ToList() ?? [];
        var target = targets.FirstOrDefault(t => string.Equals(t.TargetFramework.GetShortFolderName(), targetFramework, StringComparison.OrdinalIgnoreCase))
            ?? (targets.Count == 1 ? targets[0] : null);
        if (lockFile is null || target is null)
        {
            return None;
        }

        static bool IsPackage(string? type) => string.Equals(type, "package", StringComparison.OrdinalIgnoreCase);
        var libraries = target.Libraries.Where(l => IsPackage(l.Type) && l.Name is not null && l.Version is not null)
            .ToDictionary(l => $"{l.Name}/{l.Version!.ToNormalizedString()}", l => l, StringComparer.OrdinalIgnoreCase);
        var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in lockFile.Libraries.Where(l => IsPackage(l.Type) && l.Path is not null))
        {
            if (!libraries.TryGetValue($"{library.Name}/{library.Version.ToNormalizedString()}", out var resolved))
            {
                continue;
            }

            var folder = library.Path.Replace('\\', '/').Trim('/');
            foreach (var asset in resolved.CompileTimeAssemblies.Select(a => a.Path.Replace('\\', '/')).Where(a => !a.EndsWith("/_._", StringComparison.Ordinal)))
            {
                assemblies[$"/{folder}/{asset}".ToLowerInvariant()] = $"{library.Name} {library.Version.ToNormalizedString()}";
            }
        }

        var frameworkAssemblies = libraries.Values
            .SelectMany(l => l.FrameworkAssemblies.Select(a => (Assembly: a, Package: $"{l.Name} {l.Version!.ToNormalizedString()}")))
            .GroupBy(a => a.Assembly, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(a => a.Package).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], StringComparer.OrdinalIgnoreCase);
        return new RestoredPackages { Assemblies = assemblies, FrameworkAssemblies = frameworkAssemblies };
    }
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
    /// file), under <c>obj/</c>, and in the folder the compiler writes the assembly to (the
    /// intermediate output path, wherever the project puts it: the SDK's <c>AssemblyInfo</c>,
    /// <c>AssemblyAttributes</c>, and <c>GlobalUsings</c> files) are the build's own and are left out.
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
            var generated = GeneratedFolder(arguments.OutputDirectory, directory);
            result[target] = new CompileSet
            {
                TargetFramework = target,
                Sources = arguments.SourceFiles
                    .Where(s => Normalize(s.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    .Where(s => generated is null || !Normalize(s.Path).StartsWith(generated, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// The folder the compiler writes the assembly to (<c>/out:</c>, the build's intermediate output path) with a
    /// trailing slash, where the build writes the sources it generates; null when it is the project's folder or
    /// one above it, which hold the project's own sources.
    /// </summary>
    private static string? GeneratedFolder(string? outputDirectory, string projectDirectory)
    {
        if (string.IsNullOrEmpty(outputDirectory))
        {
            return null;
        }

        var folder = Normalize(outputDirectory).TrimEnd('/') + "/";
        var project = Normalize(projectDirectory).TrimEnd('/') + "/";
        return project.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? null : folder;
    }

    /// <summary>
    /// How the converted compile set differs. Changes the build's tools explain are allowed and
    /// reported with their evidence (docs/decisions/0061-modernize-verification.md):
    /// the SDK's implicit framework references; a restored package's compile assemblies; framework
    /// assemblies restored packages declare; .NET Standard facades the legacy build added. Any other
    /// added or removed reference is a difference.
    /// </summary>
    /// <param name="before">The original project's compile set.</param>
    /// <param name="after">The converted project's.</param>
    /// <param name="convertedPackages">Packages the conversion turned into PackageReference items, for when the assets file cannot be read.</param>
    /// <param name="restored">What the converted project's restore resolved.</param>
    public static CompileSetDifference Compare(CompileSet before, CompileSet after, IReadOnlyCollection<(string Id, string Version)>? convertedPackages = null,
        RestoredPackages? restored = null)
    {
        restored ??= RestoredPackages.None;
        var folders = (convertedPackages ?? []).Select(p => (Folder: $"/{p.Id}/{p.Version}/".ToLowerInvariant(), Package: $"{p.Id} {p.Version}")).ToList();
        string? PackageOf(string name)
        {
            if (!after.ReferencePaths.TryGetValue(name, out var path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/').ToLowerInvariant();
            return restored.Assemblies.Where(a => normalized.EndsWith(a.Key, StringComparison.Ordinal)).Select(a => a.Value)
                .Concat(folders.Where(f => normalized.Contains(f.Folder, StringComparison.Ordinal)).Select(f => f.Package))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }
        IReadOnlyList<string>? DeclaredBy(string name) => restored.FrameworkAssemblies.TryGetValue(name, out var packages) ? packages : null;
        string? FacadesOf(string name) => before.ReferencePaths.TryGetValue(name, out var path) ? FacadeFolder(path) : null;

        var added = after.References.Except(before.References, StringComparer.OrdinalIgnoreCase)
            .Where(r => !SdkImplicitFrameworkReferences.Contains(r))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removed = before.References.Except(after.References, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var explained = new List<(string Change, string Reason, string Source, string Reference)>();
        var transitive = new List<string>();
        var framework = new List<string>();
        var facades = new List<string>();
        var referencesAdded = new List<string>();
        var referencesRemoved = new List<string>();
        foreach (var reference in added)
        {
            if (PackageOf(reference) is { } package)
            {
                transitive.Add(reference);
                explained.Add(("added", "package", package, reference));
            }
            else if (DeclaredBy(reference) is { } packages)
            {
                framework.Add(reference);
                explained.Add(("added", "frameworkAssemblies", string.Join(", ", packages), reference));
            }
            else
            {
                referencesAdded.Add(reference);
            }
        }

        foreach (var reference in removed)
        {
            if (FacadesOf(reference) is { } folder)
            {
                facades.Add(reference);
                explained.Add(("removed", "facades", folder, reference));
            }
            else
            {
                referencesRemoved.Add(reference);
            }
        }

        return new CompileSetDifference
        {
            TargetFramework = before.TargetFramework,
            SourcesAdded = Sorted(after.Sources.Except(before.Sources, StringComparer.Ordinal)),
            SourcesRemoved = Sorted(before.Sources.Except(after.Sources, StringComparer.Ordinal)),
            ReferencesAdded = referencesAdded,
            ImplicitReferencesAdded = [.. after.References.Except(before.References, StringComparer.OrdinalIgnoreCase).Where(SdkImplicitFrameworkReferences.Contains).Order(StringComparer.OrdinalIgnoreCase)],
            TransitiveReferencesAdded = transitive,
            FrameworkReferencesAdded = framework,
            ReferencesRemoved = referencesRemoved,
            FacadesRemoved = facades,
            Explanations = [.. explained
                .GroupBy(e => (e.Change, e.Reason, e.Source))
                .Select(g => new ReferenceExplanation(g.Key.Change, g.Key.Reason, g.Key.Source, [.. g.Select(e => e.Reference).Order(StringComparer.OrdinalIgnoreCase)]))
                .OrderBy(e => e.Change, StringComparer.Ordinal).ThenBy(e => e.Reason, StringComparer.Ordinal).ThenBy(e => e.Source, StringComparer.Ordinal)],
            ResourcesAdded = Sorted(after.Resources.Except(before.Resources, StringComparer.Ordinal)),
            ResourcesRemoved = Sorted(before.Resources.Except(after.Resources, StringComparer.Ordinal)),
        };
    }

    /// <summary>
    /// The facades folder a reference came from, from its recognizable part on
    /// (<c>.NETFramework/v4.7.2/Facades</c>, <c>Microsoft.NET.Build.Extensions/net461/lib</c>), else null.
    /// </summary>
    private static string? FacadeFolder(string path)
    {
        var directory = (Path.GetDirectoryName(path.Replace('\\', '/')) ?? "").Replace('\\', '/');
        var extensions = directory.IndexOf("/Microsoft.NET.Build.Extensions/", StringComparison.OrdinalIgnoreCase);
        if (extensions >= 0)
        {
            return directory[(extensions + 1)..];
        }

        if (!string.Equals(Path.GetFileName(directory), "Facades", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var framework = directory.IndexOf("/.NETFramework/", StringComparison.OrdinalIgnoreCase);
        return framework >= 0 ? directory[(framework + 1)..] : string.Join('/', directory.Split('/').TakeLast(2));
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
