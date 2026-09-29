using Offramp.Core.Configuration;

namespace Offramp.NuGet.Rules;

/// <summary>
/// The packages to look for an assembly in (<c>deps resolve-dlls</c>): <c>deps.assemblyPackages</c>
/// from offramp.yml first, then rules/assembly-packages.yml, then the package whose id is the
/// assembly name. Case-insensitive, each id once.
/// </summary>
public sealed class AssemblyPackages
{
    private readonly Dictionary<string, List<string>> _packages = new(StringComparer.OrdinalIgnoreCase);

    public AssemblyPackages(IEnumerable<AssemblyPackageEntry> configured)
    {
        foreach (var entry in configured)
        {
            Add(entry.Assembly, entry.Package);
        }

        foreach (var entry in RuleFiles.Load("assembly-packages.yml")["assemblies"]!.AsArray())
        {
            Add(entry!["assembly"]!.GetValue<string>(), entry["package"]!.GetValue<string>());
        }
    }

    /// <summary>The package ids to search for the assembly, in precedence order; the last is the assembly name itself.</summary>
    public IReadOnlyList<string> For(string assembly) =>
        [.. (_packages.TryGetValue(assembly, out var ids) ? ids : []).Append(assembly).Distinct(StringComparer.OrdinalIgnoreCase)];

    private void Add(string assembly, string package)
    {
        if (assembly.Length == 0 || package.Length == 0)
        {
            return;
        }

        if (!_packages.TryGetValue(assembly, out var ids))
        {
            _packages[assembly] = ids = [];
        }

        ids.Add(package);
    }
}
