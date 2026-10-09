using System.Xml.Linq;
using NuGet.Versioning;
using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>Which Newtonsoft.Json release a project uses, how it references it, and its own redirects.</summary>
public static class NewtonsoftReferences
{
    public const string PackageId = "Newtonsoft.Json";

    private static readonly XNamespace AssemblyBinding = "urn:schemas-microsoft-com:asm.v1";

    /// <summary>The reference for a target framework, or null when the project does not use Newtonsoft.Json.</summary>
    public static NewtonsoftReference? For(ProjectInfo project, string tfm)
    {
        var resolved = project.Resolved.TryGetValue(tfm, out var framework)
            ? framework.Packages.FirstOrDefault(p => Is(p.Id))
            : null;
        var hint = project.AssemblyReferences.FirstOrDefault(r => Is(r.Name) && r.HintPath is not null)?.HintPath;

        if ((project.PackagesConfigPackages ?? []).FirstOrDefault(p => Is(p.Id)) is { } listed)
        {
            return new NewtonsoftReference { Version = listed.Version, Via = NewtonsoftReferenceKind.PackagesConfig, HintPath = hint };
        }

        if (project.PackageReferences.FirstOrDefault(p => Is(p.Id)) is { } reference)
        {
            var own = reference.VersionOverride ?? reference.Version;
            return new NewtonsoftReference
            {
                Version = resolved?.Version ?? own ?? "",
                Via = own is null || reference.VersionOverride is not null ? NewtonsoftReferenceKind.CentralPackage : NewtonsoftReferenceKind.PackageReference,
            };
        }

        if (resolved is not null)
        {
            return new NewtonsoftReference { Version = resolved.Version, Via = NewtonsoftReferenceKind.Transitive };
        }

        if (hint is not null)
        {
            return FolderVersion(hint) is { } version
                ? new NewtonsoftReference { Version = version, Via = NewtonsoftReferenceKind.HintPath, HintPath = hint }
                : new NewtonsoftReference
                {
                    Version = project.AssemblyReferences.First(r => r.HintPath == hint).Metadata?.AssemblyVersion ?? "",
                    Via = NewtonsoftReferenceKind.LooseDll,
                    HintPath = hint,
                };
        }

        return null;
    }

    /// <summary>The release, as NuGet orders it; null for a loose DLL whose version could not be read.</summary>
    public static NuGetVersion? Release(NewtonsoftReference reference) =>
        NuGetVersion.TryParse(reference.Version, out var version) ? version : null;

    /// <summary>Newtonsoft.Json's redirects in the project's own app.config (web.config for web projects).</summary>
    public static IReadOnlyList<NewtonsoftRedirect> Redirects(string repositoryRoot, ProjectInfo project)
    {
        var folder = project.Id.Contains('/', StringComparison.Ordinal) ? project.Id[..project.Id.LastIndexOf('/')] : "";
        var name = project.Kind == ProjectKind.Web ? "web.config" : "app.config";
        var directory = RepoPaths.ToAbsolute(repositoryRoot, folder.Length == 0 ? "." : folder);
        var file = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (file is null)
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Load(file);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var relative = (folder.Length == 0 ? "" : folder + "/") + Path.GetFileName(file);
        return
        [
            .. document.Descendants(AssemblyBinding + "dependentAssembly")
                .Where(d => Is((string?)d.Element(AssemblyBinding + "assemblyIdentity")?.Attribute("name") ?? ""))
                .Select(d => d.Element(AssemblyBinding + "bindingRedirect"))
                .OfType<XElement>()
                .Select(r => new NewtonsoftRedirect(relative, (string?)r.Attribute("oldVersion") ?? "", (string?)r.Attribute("newVersion") ?? "")),
        ];
    }

    private static bool Is(string id) => string.Equals(id, PackageId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The version in a <c>packages/Newtonsoft.Json.&lt;version&gt;/</c> folder of a HintPath.</summary>
    private static string? FolderVersion(string hintPath)
    {
        const string prefix = PackageId + ".";
        return hintPath.Replace('\\', '/').Split('/')
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && NuGetVersion.TryParse(s[prefix.Length..], out _))
            .Select(s => s[prefix.Length..])
            .FirstOrDefault();
    }
}
