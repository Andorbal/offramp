using System.Xml;
using System.Xml.Linq;
using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Analysis.DeadCode;

/// <summary>Which rule says other repositories may use a project's public API.</summary>
public enum ShippedRule
{
    /// <summary>Listed in <c>deadCode.externalConsumers</c>.</summary>
    ExternalConsumer,

    /// <summary><c>IsPackable=true</c>.</summary>
    Packable,

    /// <summary>A <c>.nuspec</c> anywhere in the repository packs its DLL, or (for a library) a <c>.nuspec</c> or <c>.nuspec.template</c> sits in its folder.</summary>
    Nuspec,

    /// <summary>A library no application in the solution depends on, directly or through other libraries.</summary>
    NoApplication,
}

/// <summary>Why a project's public API is shipped: the rule, and a phrase naming the evidence.</summary>
public sealed record ShippedReason(ShippedRule Rule, string Reason);

/// <summary>
/// The projects whose public API other repositories may use (ADR 0041): listed in
/// <c>deadCode.externalConsumers</c>, packable, packed from a <c>.nuspec</c>, or a library no
/// application in the solution depends on. <c>audit dead-code</c> never rates their public
/// symbols <c>high</c>, and <c>move tests</c> never moves their public types.
/// </summary>
public sealed class ShippedProjects
{
    /// <summary>Kinds that run: a project of one of them is an application, not a library someone else consumes.</summary>
    private static readonly HashSet<ProjectKind> ApplicationKinds = [ProjectKind.Web, ProjectKind.Winforms, ProjectKind.Wpf, ProjectKind.Service, ProjectKind.Console];

    private readonly Dictionary<string, ShippedReason> _shipped;

    private ShippedProjects(Dictionary<string, ShippedReason> shipped) => _shipped = shipped;

    /// <summary>Why the project is shipped, or null when nothing says so.</summary>
    public ShippedReason? Of(ProjectInfo project) => _shipped.GetValueOrDefault(project.Id);

    /// <param name="root">The repository root, searched for <c>.nuspec</c> files (outside <c>bin</c>, <c>obj</c>, <c>packages</c>, and dot folders).</param>
    /// <param name="model">The workspace model.</param>
    /// <param name="externalConsumers"><c>deadCode.externalConsumers</c>: project names, assembly names, or project paths.</param>
    public static ShippedProjects Read(string root, WorkspaceModel model, IReadOnlyList<string> externalConsumers)
    {
        var nuspecs = Nuspecs(root);
        var usedByApplication = UsedByApplications(model);
        var shipped = new Dictionary<string, ShippedReason>(StringComparer.Ordinal);
        foreach (var project in model.Projects)
        {
            if (Why(project, externalConsumers, nuspecs, usedByApplication) is { } reason)
            {
                shipped[project.Id] = reason;
            }
        }

        return new ShippedProjects(shipped);
    }

    private static ShippedReason? Why(ProjectInfo project, IReadOnlyList<string> externalConsumers, List<Nuspec> nuspecs, HashSet<string> usedByApplication)
    {
        if (externalConsumers.Any(c => string.Equals(c, project.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c, project.AssemblyName, StringComparison.OrdinalIgnoreCase) || string.Equals(c, project.Id, StringComparison.Ordinal)))
        {
            return new ShippedReason(ShippedRule.ExternalConsumer, "listed in deadCode.externalConsumers");
        }

        if (project.Properties.TryGetValue("IsPackable", out var packable) && string.Equals(packable, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new ShippedReason(ShippedRule.Packable, "packable (IsPackable)");
        }

        // A nuspec beside an application packs it for deployment (OctoPack), not for other code to use.
        var library = IsLibrary(project);
        var folder = Folder(project.Id);
        if (library && nuspecs.FirstOrDefault(n => string.Equals(Folder(n.Path), folder, StringComparison.OrdinalIgnoreCase)) is { } beside)
        {
            return new ShippedReason(ShippedRule.Nuspec, $"packed by {beside.Path}");
        }

        var dll = (project.AssemblyName ?? project.Name) + ".dll";
        if (nuspecs.FirstOrDefault(n => n.Files.Contains(dll)) is { } naming)
        {
            return new ShippedReason(ShippedRule.Nuspec, $"packed as {dll} by {naming.Path}");
        }

        return !library || usedByApplication.Contains(project.Id) ? null : new ShippedReason(ShippedRule.NoApplication, "no application in the solution uses it");
    }

    /// <summary>
    /// A library: <c>kind: library</c>, or a <c>test</c> project whose output is a library (a
    /// production project that carries its tests, which is what <c>move tests</c> works on).
    /// </summary>
    private static bool IsLibrary(ProjectInfo project) =>
        project.Kind == ProjectKind.Library
        || (project.Kind == ProjectKind.Test && (project.OutputType is null || string.Equals(project.OutputType, "Library", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The projects an application depends on, directly or through other projects.</summary>
    private static HashSet<string> UsedByApplications(WorkspaceModel model)
    {
        var byId = model.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(model.Projects.Where(p => ApplicationKinds.Contains(p.Kind)).SelectMany(p => p.ProjectReferences));
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (used.Add(next) && byId.TryGetValue(next, out var project))
            {
                foreach (var reference in project.ProjectReferences)
                {
                    pending.Push(reference);
                }
            }
        }

        return used;
    }

    /// <summary>A <c>.nuspec</c> file (repository-relative) and the file names its <c>&lt;file src&gt;</c> entries pack.</summary>
    private sealed record Nuspec(string Path, HashSet<string> Files);

    /// <summary>Every <c>.nuspec</c> and <c>.nuspec.template</c> in the repository, sorted by path.</summary>
    private static List<Nuspec> Nuspecs(string root)
    {
        var nuspecs = new List<Nuspec>();
        foreach (var path in ProjectFiles.Walk(root))
        {
            var name = System.IO.Path.GetFileName(path);
            if (name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".nuspec.template", StringComparison.OrdinalIgnoreCase))
            {
                nuspecs.Add(new Nuspec(RepoPaths.ToRepositoryRelative(root, path), PackedFiles(path)));
            }
        }

        return [.. nuspecs.OrderBy(n => n.Path, StringComparer.Ordinal)];
    }

    /// <summary>The file names (without folders) that <c>&lt;file src="…"&gt;</c> entries name literally; wildcards name nothing.</summary>
    private static HashSet<string> PackedFiles(string path)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "file"))
            {
                var source = (string?)file.Attribute("src");
                var name = source?.Replace('\\', '/').Split('/')[^1];
                if (name is { Length: > 0 } && name.IndexOfAny(['*', '?']) < 0)
                {
                    files.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // A template that is not XML yet still counts for the project folder it sits in.
        }

        return files;
    }

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
