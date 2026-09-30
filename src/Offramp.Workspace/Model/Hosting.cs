using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Model;

/// <summary>
/// Hosted projects (docs/decisions/0055-hosted-projects-belong-to-their-host.md): a web or library project whose
/// output folder lies inside a web project's folder, outside its own, is loaded by that web application at run time
/// (SmartStoreNET's plugins in <c>SmartStore.Web/Plugins/&lt;Name&gt;/</c>, its admin area in <c>SmartStore.Web/bin/</c>,
/// DotNetNuke's modules in <c>Website/bin/</c>). It is part of that application, not one of its own.
/// </summary>
public static class Hosting
{
    /// <summary>The projects with <see cref="ProjectInfo.HostedBy"/> set where the rule holds, in the same order.</summary>
    /// <param name="projects">The model's projects.</param>
    /// <param name="repositoryRoot">The local repository, where <c>Global.asax</c> files are looked for.</param>
    /// <param name="copiedTo">Project id → repository-relative folders its own build copied its assembly to (see <see cref="CopiedTo"/>).</param>
    public static List<ProjectInfo> Detect(
        IReadOnlyList<ProjectInfo> projects, string repositoryRoot, IReadOnlyDictionary<string, IReadOnlyList<string>>? copiedTo = null)
    {
        var webs = projects.Where(p => p.Kind == ProjectKind.Web).ToList();
        var referenced = projects.SelectMany(p => p.ProjectReferences).ToHashSet(StringComparer.Ordinal);
        return [.. projects.Select(p => HostOf(p, webs, referenced, repositoryRoot, copiedTo?.GetValueOrDefault(p.Id) ?? []) is { } host ? p with { HostedBy = host } : p)];
    }

    /// <summary>
    /// The folders each project's own build copied its assembly to, repository-relative and sorted: DotNetNuke's modules
    /// build into their own <c>bin/</c> and an <c>AfterBuild</c> copy puts the assembly into the site's.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CopiedTo(IEnumerable<AssemblyCopy> copies, CapturePathMapper mapper) =>
        copies
            .Select(c => (Project: mapper.ToRelative(c.ProjectFile), Folder: DestinationFolder(c, mapper)))
            .Where(c => c.Project is not null && c.Folder is not null)
            .GroupBy(c => c.Project!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)[.. g.Select(c => c.Folder!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                StringComparer.Ordinal);

    /// <summary>True when another web project hosts the project.</summary>
    public static bool IsHosted(ProjectInfo project) => project.HostedBy is not null;

    /// <summary>The projects <paramref name="host"/> hosts, directly or through a project it hosts, sorted.</summary>
    public static IReadOnlyList<string> HostedProjects(WorkspaceModel model, string host)
    {
        var byHost = model.Projects.Where(p => p.HostedBy is not null).ToLookup(p => p.HostedBy!.Project, p => p.Id, StringComparer.Ordinal);
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(byHost[host]);
        while (pending.TryPop(out var id))
        {
            if (id != host && seen.Add(id))
            {
                foreach (var next in byHost[id])
                {
                    pending.Push(next);
                }
            }
        }

        return [.. seen];
    }

    /// <summary>
    /// What an application needs to run: <paramref name="start"/>, the projects it hosts, and everything they depend
    /// on. A project reached through a reference brings its dependencies, not the projects its own host loads.
    /// </summary>
    public static HashSet<string> ApplicationClosure(WorkspaceModel model, string start)
    {
        var dependencies = model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([start, .. HostedProjects(model, start)]);
        while (pending.TryPop(out var id))
        {
            if (seen.Add(id))
            {
                foreach (var next in dependencies[id])
                {
                    pending.Push(next);
                }
            }
        }

        return seen;
    }

    /// <summary>The destination's folder, resolved against the project's folder when the log records it relative.</summary>
    private static string? DestinationFolder(AssemblyCopy copy, CapturePathMapper mapper) =>
        Path.GetDirectoryName(copy.Destination.Replace('\\', '/')) is { } folder
            ? mapper.ToRelative(Path.GetDirectoryName(copy.ProjectFile.Replace('\\', '/')) ?? "", folder.Length == 0 ? "." : folder)
            : null;

    private static ProjectHost? HostOf(
        ProjectInfo project, List<ProjectInfo> webs, HashSet<string> referenced, string repositoryRoot, IReadOnlyList<string> copiedTo)
    {
        if (project.Kind is not (ProjectKind.Web or ProjectKind.Library))
        {
            return null;
        }

        // Where the assembly lands: the output folder first, then the folders the project's build copies it to.
        var own = Folder(project.Id);
        var landings = (project.OutputPath is { } output ? [(Folder: output, How: "builds into")] : Array.Empty<(string Folder, string How)>())
            .Concat(copiedTo.Select(f => (Folder: f, How: "its build copies its assembly into")))
            .Where(l => !Inside(l.Folder, own));

        // The deepest web project whose folder holds it: an area nested in a site hosts what builds into it.
        var (host, landing) = landings
            .SelectMany(l => webs.Where(w => w.Id != project.Id && Inside(l.Folder, Folder(w.Id))).Select(w => (Web: w, Landing: l)))
            .OrderByDescending(c => Folder(c.Web.Id).Length)
            .ThenBy(c => c.Web.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        // A Global.asax is an HttpApplication of its own: that project starts an application wherever it builds to.
        if (host is null || (project.Kind == ProjectKind.Web && HasGlobalAsax(repositoryRoot, own)))
        {
            return null;
        }

        var evidence = new List<string> { $"{landing.How} {landing.Folder}, inside {Folder(host.Id)}" };
        if (project.Kind == ProjectKind.Web)
        {
            evidence.Add("has no Global.asax");
        }

        if (project.ProjectReferences.Contains(host.Id, StringComparer.Ordinal))
        {
            evidence.Add($"references {host.Id}");
        }

        if (!referenced.Contains(project.Id))
        {
            evidence.Add("no project references it");
        }

        return new ProjectHost { Project = host.Id, Evidence = evidence };
    }

    private static string Folder(string projectId) => RepoPaths.Normalize(Path.GetDirectoryName(projectId) ?? "");

    private static bool Inside(string path, string folder) =>
        folder.Length == 0
        || path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasGlobalAsax(string repositoryRoot, string folder)
    {
        var directory = RepoPaths.ToAbsolute(repositoryRoot, folder.Length == 0 ? "." : folder);
        return Directory.Exists(directory)
            && Directory.EnumerateFiles(directory).Any(f => string.Equals(Path.GetFileName(f), "Global.asax", StringComparison.OrdinalIgnoreCase));
    }
}
