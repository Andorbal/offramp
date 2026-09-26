using Offramp.Core.Configuration;

namespace Offramp.NuGet.Rules;

/// <summary>A known successor for a Framework-era package.</summary>
public sealed record PackageReplacement(string Match, string Replacement, string Source);

/// <summary>
/// The package successor table: rules/package-map.yml, then <c>deps.packageMap</c> from
/// offramp.yml on top. An exact id beats any prefix; the longest prefix wins among prefixes;
/// configuration beats the built-in table for the same key.
/// </summary>
public sealed class PackageMap
{
    private readonly Dictionary<string, PackageReplacement> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageReplacement> _prefixes = new(StringComparer.OrdinalIgnoreCase);

    public PackageMap(IEnumerable<PackageMapEntry> configured)
    {
        foreach (var entry in RuleFiles.Load("package-map.yml")["packages"]!.AsArray())
        {
            Add(entry!["package"]?.GetValue<string>(), entry["prefix"]?.GetValue<string>(), entry["replacement"]!.GetValue<string>(), "rules/package-map.yml");
        }

        foreach (var entry in configured)
        {
            Add(entry.Package, entry.Prefix, entry.Replacement, "offramp.yml");
        }
    }

    public PackageReplacement? Find(string packageId)
    {
        if (_packages.TryGetValue(packageId, out var exact))
        {
            return exact;
        }

        return _prefixes
            .Where(p => packageId.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Length)
            .Select(p => p.Value)
            .FirstOrDefault();
    }

    private void Add(string? package, string? prefix, string replacement, string source)
    {
        if (package is not null)
        {
            _packages[package] = new PackageReplacement(package, replacement, source);
        }
        else if (prefix is not null)
        {
            _prefixes[prefix] = new PackageReplacement(prefix + "*", replacement, source);
        }
    }
}
