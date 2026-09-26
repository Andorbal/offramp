using NuGet.Configuration;
using Offramp.Core.Model;

namespace Offramp.Refactoring.Moves;

/// <summary>A package a project resolved, and its folder in the global packages folder when it is there.</summary>
public sealed record IndexedPackage(string Id, string Version, string? Folder);

/// <summary>
/// Which of a project's resolved packages supplies an assembly. Compiler logs record
/// references by file name only, so the index reads each package's <c>lib/</c> and
/// <c>ref/</c> folders in the global packages folder; a package that is not there
/// supplies the assembly named like itself.
/// </summary>
public sealed class PackageIndex
{
    private readonly Dictionary<string, IndexedPackage> _byAssembly;

    private PackageIndex(Dictionary<string, IndexedPackage> byAssembly) => _byAssembly = byAssembly;

    public static PackageIndex For(string repositoryRoot, ProjectInfo project)
    {
        string? root;
        try
        {
            root = SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(repositoryRoot));
        }
        catch (NuGetConfigurationException)
        {
            root = null;
        }

        var byAssembly = new Dictionary<string, IndexedPackage>(StringComparer.OrdinalIgnoreCase);
        var packages = project.Resolved.Values.SelectMany(f => f.Packages)
            .DistinctBy(p => (p.Id.ToLowerInvariant(), p.Version))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var folder = root is null ? null : Path.Combine(root, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
            var indexed = new IndexedPackage(package.Id, package.Version, folder is not null && Directory.Exists(folder) ? folder : null);
            byAssembly.TryAdd(package.Id, indexed);
            if (indexed.Folder is null)
            {
                continue;
            }

            foreach (var kind in new[] { "ref", "lib" })
            {
                var assets = Path.Combine(indexed.Folder, kind);
                if (!Directory.Exists(assets))
                {
                    continue;
                }

                foreach (var dll in Directory.EnumerateFiles(assets, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!byAssembly.TryGetValue(name, out var existing) || !string.Equals(existing.Id, name, StringComparison.OrdinalIgnoreCase))
                    {
                        byAssembly[name] = string.Equals(package.Id, name, StringComparison.OrdinalIgnoreCase) || existing is null ? indexed : existing;
                    }
                }
            }
        }

        return new PackageIndex(byAssembly);
    }

    /// <summary>The package supplying an assembly, or null (a .NET Framework or project reference).</summary>
    public IndexedPackage? Find(string assemblyName) => _byAssembly.GetValueOrDefault(assemblyName);
}
