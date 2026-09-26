using Offramp.Core.Configuration;

namespace Offramp.Scaffolding;

/// <summary>Pinned versions for the packages generated projects reference (<c>rules/scaffold-packages.yml</c>).</summary>
public static class ScaffoldPackages
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Versions = new(Load);

    public static string Version(string id) =>
        Versions.Value.TryGetValue(id, out var version) ? version : throw new ArgumentOutOfRangeException(nameof(id), id, "Not in rules/scaffold-packages.yml.");

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(ScaffoldPackages).Assembly.GetManifestResourceStream("Offramp.Scaffolding.Rules.scaffold-packages.yml")
            ?? throw new InvalidOperationException("Missing embedded rule file scaffold-packages.yml.");
        using var reader = new StreamReader(stream);
        var packages = YamlJson.Parse(reader.ReadToEnd()).Root["packages"]!.AsObject();
        return packages.ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase);
    }
}
