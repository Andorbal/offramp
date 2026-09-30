using System.Xml.Linq;
using NuGet.Frameworks;
using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Doctor;

/// <summary>
/// What <c>doctor</c> reads from a project file itself, before there is a model or where the model's evaluations
/// do not say (a property <c>verify.properties</c> overrides): no evaluation, so conditions are ignored.
/// </summary>
internal sealed record ProjectFileFacts
{
    public required string Project { get; init; }

    /// <summary>True for a legacy (non-SDK) project: no <c>Sdk</c> attribute, <c>Sdk</c> element, or SDK import.</summary>
    public bool Legacy { get; init; }

    /// <summary>The .NET Framework targets it names (<c>TargetFrameworkVersion</c>, <c>TargetFramework(s)</c>), short names.</summary>
    public IReadOnlyList<string> NetFrameworkTargets { get; init; } = [];

    /// <summary>The properties it sets to a non-empty value, by name (case-insensitive), for the names asked about.</summary>
    public IReadOnlySet<string> SetProperties { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The facts of a C#, Visual Basic, or F# project file; null for another kind or an unreadable file.</summary>
    public static ProjectFileFacts? Read(string repositoryRoot, string project, IEnumerable<string>? properties = null)
    {
        if (!OtherProjects.IsDotNet(project))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(RepoPaths.ToAbsolute(repositoryRoot, project));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        var root = document.Root!;
        var elements = root.Descendants().ToList();
        var sdk = root.Attribute("Sdk") is not null
            || elements.Any(e => e.Name.LocalName == "Sdk" || (e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null));
        var wanted = (properties ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ProjectFileFacts
        {
            Project = project,
            Legacy = !sdk,
            NetFrameworkTargets = FrameworkTargets(elements),
            SetProperties = elements
                .Where(e => wanted.Contains(e.Name.LocalName) && e.Parent?.Name.LocalName == "PropertyGroup" && e.Value.Trim().Length > 0)
                .Select(e => e.Name.LocalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The package-folder short name of a .NET Framework target (<c>net461</c>), without a profile; null for others.</summary>
    public static string? ReferenceAssembliesName(string targetFramework)
    {
        var framework = NuGetFramework.Parse(targetFramework);
        if (framework.Framework != FrameworkConstants.FrameworkIdentifiers.Net)
        {
            return null;
        }

        var version = framework.Version;
        return $"net{version.Major}{version.Minor}{(version.Build > 0 ? version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}";
    }

    private static List<string> FrameworkTargets(List<XElement> elements)
    {
        var values = new List<string?>();
        foreach (var element in elements)
        {
            switch (element.Name.LocalName)
            {
                case "TargetFrameworkVersion":
                    values.Add(Tfm.FromIdentifier(".NETFramework", element.Value.Trim()));
                    break;
                case "TargetFramework" or "TargetFrameworks":
                    values.AddRange(element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Tfm.Normalize));
                    break;
            }
        }

        return [.. values.OfType<string>().Select(ReferenceAssembliesName).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}
