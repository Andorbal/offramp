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
    // Per key, candidates in precedence order: configuration first, then the built-in table.
    private readonly Dictionary<string, List<PackageReplacement>> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PackageReplacement>> _prefixes = new(StringComparer.OrdinalIgnoreCase);

    public PackageMap(IEnumerable<PackageMapEntry> configured)
    {
        foreach (var entry in RuleFiles.Load("package-map.yml")["packages"]!.AsArray())
        {
            Add(entry!["package"]?.GetValue<string>(), entry["prefix"]?.GetValue<string>(), entry["replacement"]!.GetValue<string>(), "rules/package-map.yml", first: false);
        }

        foreach (var entry in configured)
        {
            Add(entry.Package, entry.Prefix, entry.Replacement, "offramp.yml", first: true);
        }
    }

    /// <summary>The replacement the rules choose: <see cref="Candidates"/>' first.</summary>
    public PackageReplacement? Find(string packageId) => Candidates(packageId).FirstOrDefault();

    /// <summary>
    /// Every entry that matches, in precedence order: exact ids before prefixes, longer prefixes
    /// before shorter, configuration before the built-in table; one per distinct replacement.
    /// </summary>
    public IReadOnlyList<PackageReplacement> Candidates(string packageId)
    {
        var exact = _packages.TryGetValue(packageId, out var list) ? list : [];
        var prefixes = _prefixes
            .Where(p => packageId.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Length)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => p.Value);
        return [.. exact.Concat(prefixes).DistinctBy(r => r.Replacement, StringComparer.OrdinalIgnoreCase)];
    }

    private void Add(string? package, string? prefix, string replacement, string source, bool first)
    {
        var (table, key, match) = package is not null ? (_packages, package, package) : prefix is not null ? (_prefixes, prefix, prefix + "*") : (null, "", "");
        if (table is null)
        {
            return;
        }

        if (!table.TryGetValue(key, out var list))
        {
            table[key] = list = [];
        }

        var entry = new PackageReplacement(match, replacement, source);
        if (first)
        {
            list.Insert(0, entry);
        }
        else
        {
            list.Add(entry);
        }
    }
}
