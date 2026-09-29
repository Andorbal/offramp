using System.Xml;
using System.Xml.Linq;
using Offramp.Core.Model;

namespace Offramp.Workspace.Model;

/// <summary>Reads a packages.config: the packages a legacy project installed, direct or not.</summary>
public static class PackagesConfigReader
{
    /// <summary>The packages, sorted by id and version; null when the file is missing or is not XML.</summary>
    public static IReadOnlyList<PackagesConfigPackage>? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return null;
        }

        return [.. (document.Root?.Elements("package") ?? [])
            .Select(p => new PackagesConfigPackage
            {
                Id = p.Attribute("id")?.Value.Trim() ?? "",
                Version = p.Attribute("version")?.Value.Trim() ?? "",
                TargetFramework = p.Attribute("targetFramework")?.Value.Trim() is { Length: > 0 } tfm ? tfm : null,
                DevelopmentDependency = string.Equals(p.Attribute("developmentDependency")?.Value, "true", StringComparison.OrdinalIgnoreCase),
            })
            .Where(p => p.Id.Length > 0 && p.Version.Length > 0)
            .DistinctBy(p => (p.Id.ToLowerInvariant(), p.Version.ToLowerInvariant()))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase)];
    }
}
