using NuGet.Common;
using NuGet.ProjectModel;
using Offramp.Core.Model;

namespace Offramp.Workspace.Ingest;

/// <summary>Reads the resolved package graph from <c>project.assets.json</c> with NuGet.ProjectModel.</summary>
public static class AssetsFileReader
{
    /// <summary>Resolved packages per target framework (short folder name), or empty when the file is missing.</summary>
    public static SortedDictionary<string, ResolvedFramework> Read(string? assetsFilePath)
    {
        var result = new SortedDictionary<string, ResolvedFramework>(StringComparer.Ordinal);
        if (assetsFilePath is null || !File.Exists(assetsFilePath))
        {
            return result;
        }

        var lockFile = LockFileUtilities.GetLockFile(assetsFilePath, NullLogger.Instance);
        if (lockFile is null)
        {
            return result;
        }

        foreach (var target in lockFile.Targets.Where(t => string.IsNullOrEmpty(t.RuntimeIdentifier)))
        {
            var tfm = target.TargetFramework.GetShortFolderName();
            var (direct, autoReferenced) = DirectDependencies(lockFile, target.TargetFramework);
            var toolchain = ToolchainOnly(target, direct, autoReferenced);
            var packages = target.Libraries
                .Where(l => string.Equals(l.Type, "package", StringComparison.OrdinalIgnoreCase) && !toolchain.Contains(l.Name ?? ""))
                .Select(l => new ResolvedPackage
                {
                    Id = l.Name ?? "",
                    Version = l.Version?.ToNormalizedString() ?? "",
                    Direct = direct.Contains(l.Name ?? ""),
                    Dependencies = [.. l.Dependencies
                        .Select(d => new PackageDependency(d.Id, d.VersionRange.ToNormalizedString()))
                        .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)],
                })
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Version, StringComparer.Ordinal)
                .ToList();
            result[tfm] = new ResolvedFramework { Packages = packages };
        }

        return result;
    }

    /// <summary>
    /// The project references restore saw declared (the assets file's restore metadata, every
    /// target framework), as the paths it recorded; null when the file is missing or has none
    /// recorded. The SDK adds a project's transitive references as ProjectReference items too
    /// (<c>IncludeTransitiveProjectReferences</c>); restore lists only the declared ones.
    /// </summary>
    public static IReadOnlyList<string>? DeclaredProjectReferences(string? assetsFilePath)
    {
        if (assetsFilePath is null || !File.Exists(assetsFilePath))
        {
            return null;
        }

        var metadata = LockFileUtilities.GetLockFile(assetsFilePath, NullLogger.Instance)?.PackageSpec?.RestoreMetadata;
        if (metadata is null)
        {
            return null;
        }

        return [.. metadata.TargetFrameworks.SelectMany(t => t.ProjectReferences).Select(r => r.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Packages reachable only from dependencies the SDK adds by itself (<c>autoReferenced</c>:
    /// NETStandard.Library, Microsoft.NETFramework.ReferenceAssemblies). They are toolchain,
    /// not the project's dependencies, and some depend on the machine: the reference-assembly
    /// pack is added only where no .NET Framework targeting pack is installed.
    /// </summary>
    private static HashSet<string> ToolchainOnly(LockFileTarget target, HashSet<string> direct, HashSet<string> autoReferenced)
    {
        var dependencies = target.Libraries
            .Where(l => l.Name is not null)
            .GroupBy(l => l.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(l => l.Dependencies).Select(d => d.Id).ToList(), StringComparer.OrdinalIgnoreCase);
        var projectRoots = target.Libraries
            .Where(l => string.Equals(l.Type, "project", StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => l.Dependencies.Select(d => d.Id));

        var fromToolchain = Closure(autoReferenced, dependencies);
        var fromProject = Closure(direct.Where(d => !autoReferenced.Contains(d)).Concat(projectRoots), dependencies);
        fromToolchain.ExceptWith(fromProject);
        return fromToolchain;
    }

    private static HashSet<string> Closure(IEnumerable<string> roots, Dictionary<string, List<string>> dependencies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (seen.Add(name) && dependencies.TryGetValue(name, out var next))
            {
                foreach (var dependency in next)
                {
                    pending.Push(dependency);
                }
            }
        }

        return seen;
    }

    private static (HashSet<string> Direct, HashSet<string> AutoReferenced) DirectDependencies(LockFile lockFile, NuGet.Frameworks.NuGetFramework framework)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var auto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spec = lockFile.PackageSpec?.TargetFrameworks.FirstOrDefault(t => t.FrameworkName.Equals(framework));
        if (spec is not null)
        {
            foreach (var dependency in spec.Dependencies)
            {
                names.Add(dependency.Name);
                if (dependency.AutoReferenced)
                {
                    auto.Add(dependency.Name);
                }
            }
        }

        return (names, auto);
    }
}
