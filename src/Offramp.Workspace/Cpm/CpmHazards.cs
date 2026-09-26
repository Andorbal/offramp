using System.Xml.Linq;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Cpm;

public sealed record CpmHazard(DiagnosticDescriptor Descriptor, string Path, string Message);

/// <summary>
/// Central package management preflight (docs/spec/commands/deps.md, CPM preflight):
/// projects outside the solution that would inherit a Directory.Packages.props
/// (OFR1301), nested props files that shadow the root without importing it
/// (OFR1302), and solution projects still on packages.config (OFR1303).
/// </summary>
public static class CpmHazards
{
    private const string PropsName = "Directory.Packages.props";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "TestResults", "artifacts",
    };

    public static IReadOnlyList<CpmHazard> Find(string repositoryRoot, IReadOnlyCollection<string> solutionProjects)
    {
        var inSolution = solutionProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var props = new List<string>();
        var projects = new List<string>();
        Walk(repositoryRoot, repositoryRoot, props, projects);

        var hazards = new List<CpmHazard>();
        var propsDirectories = props.Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "").ToList();

        foreach (var project in projects.Where(p => !inSolution.Contains(p)))
        {
            var governing = Governing(project, propsDirectories);
            if (governing is not null)
            {
                var file = governing.Length == 0 ? PropsName : governing + "/" + PropsName;
                hazards.Add(new CpmHazard(DiagnosticCatalog.OFR1301, project,
                    $"Not in the solution, but {file} applies to it: its PackageReference versions would stop working under central package management."));
            }
        }

        foreach (var nested in props.Where(p => p.Contains('/', StringComparison.Ordinal)))
        {
            var directory = Path.GetDirectoryName(nested)!.Replace('\\', '/');
            var parent = Governing(directory, propsDirectories.Where(d => d != directory).ToList());
            if (parent is not null && !ImportsParent(RepoPaths.ToAbsolute(repositoryRoot, nested)))
            {
                var parentFile = parent.Length == 0 ? PropsName : parent + "/" + PropsName;
                hazards.Add(new CpmHazard(DiagnosticCatalog.OFR1302, nested,
                    $"Shadows {parentFile} for the projects below it and does not import it."));
            }
        }

        foreach (var project in solutionProjects.Order(StringComparer.Ordinal))
        {
            var directory = Path.GetDirectoryName(RepoPaths.ToAbsolute(repositoryRoot, project))!;
            if (File.Exists(Path.Combine(directory, "packages.config")))
            {
                hazards.Add(new CpmHazard(DiagnosticCatalog.OFR1303, project,
                    "Uses packages.config, which central package management does not apply to."));
            }
        }

        return [.. hazards.OrderBy(h => h.Descriptor.Code, StringComparer.Ordinal).ThenBy(h => h.Path, StringComparer.Ordinal)];
    }

    /// <summary>The nearest directory at or above <paramref name="path"/>'s directory that has a props file.</summary>
    private static string? Governing(string path, IReadOnlyList<string> propsDirectories)
    {
        var directory = path.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path)?.Replace('\\', '/') ?? ""
            : path;
        while (true)
        {
            if (propsDirectories.Contains(directory))
            {
                return directory;
            }

            if (directory.Length == 0)
            {
                return null;
            }

            var slash = directory.LastIndexOf('/');
            directory = slash < 0 ? "" : directory[..slash];
        }
    }

    private static bool ImportsParent(string propsFile)
    {
        try
        {
            var document = XDocument.Load(propsFile);
            return document.Descendants()
                .Where(e => e.Name.LocalName == "Import")
                .Any(e => (e.Attribute("Project")?.Value ?? "").Contains(PropsName, StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static void Walk(string root, string directory, List<string> props, List<string> projects)
    {
        IEnumerable<string> files, subdirectories;
        try
        {
            files = Directory.EnumerateFiles(directory);
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Equals(PropsName, StringComparison.OrdinalIgnoreCase))
            {
                props.Add(RepoPaths.ToRepositoryRelative(root, file));
            }
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(RepoPaths.ToRepositoryRelative(root, file));
            }
        }

        foreach (var sub in subdirectories)
        {
            var name = Path.GetFileName(sub);
            if (!name.StartsWith('.') && !SkippedDirectories.Contains(name))
            {
                Walk(root, sub, props, projects);
            }
        }
    }
}
