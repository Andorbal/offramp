using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Analysis.Rules;

[JsonConverter(typeof(KebabCaseEnumConverter<FrameworkAssemblyKind>))]
public enum FrameworkAssemblyKind
{
    Builtin,
    Package,
    CompatPack,
    None,
    Unknown,
}

/// <summary>The modern equivalent of a .NET Framework assembly (rules/framework-assemblies.yml).</summary>
public sealed record FrameworkAssemblyMapping(FrameworkAssemblyKind Kind, string? Package, bool WindowsOnly, string? Note);

public static class FrameworkAssemblyMap
{
    private static readonly Lazy<(Dictionary<string, FrameworkAssemblyMapping> Exact, List<(string Prefix, FrameworkAssemblyMapping Mapping)> Prefixes)> Table = new(Load);

    public static readonly FrameworkAssemblyMapping Unknown = new(FrameworkAssemblyKind.Unknown, null, false, "Not in rules/framework-assemblies.yml.");

    public static FrameworkAssemblyMapping Find(string assemblyName)
    {
        var (exact, prefixes) = Table.Value;
        if (exact.TryGetValue(assemblyName, out var mapping))
        {
            return mapping;
        }

        return prefixes
            .Where(p => assemblyName.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Prefix.Length)
            .Select(p => p.Mapping)
            .FirstOrDefault() ?? Unknown;
    }

    private static (Dictionary<string, FrameworkAssemblyMapping>, List<(string, FrameworkAssemblyMapping)>) Load()
    {
        var exact = new Dictionary<string, FrameworkAssemblyMapping>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new List<(string, FrameworkAssemblyMapping)>();
        foreach (var (name, node) in RuleFiles.Load("framework-assemblies.yml")["assemblies"]!.AsObject())
        {
            var kind = node!["kind"]!.GetValue<string>() switch
            {
                "builtin" => FrameworkAssemblyKind.Builtin,
                "package" => FrameworkAssemblyKind.Package,
                "compat-pack" => FrameworkAssemblyKind.CompatPack,
                "none" => FrameworkAssemblyKind.None,
                var other => throw new InvalidDataException($"rules/framework-assemblies.yml: unknown kind '{other}' for {name}."),
            };
            var mapping = new FrameworkAssemblyMapping(
                kind,
                node["package"]?.GetValue<string>() ?? (kind == FrameworkAssemblyKind.CompatPack ? "Microsoft.Windows.Compatibility" : null),
                node["windowsOnly"]?.GetValue<bool>() ?? false,
                node["note"]?.GetValue<string>());
            if (name.EndsWith('*'))
            {
                prefixes.Add((name[..^1], mapping));
            }
            else
            {
                exact[name] = mapping;
            }
        }

        return (exact, prefixes);
    }
}
