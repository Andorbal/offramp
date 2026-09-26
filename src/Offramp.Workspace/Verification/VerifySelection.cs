using System.Globalization;
using Offramp.Core.Configuration;
using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Verification;

/// <summary>What to verify: the projects, whether that is the whole solution, and why.</summary>
public sealed record VerifySelection(IReadOnlyList<string> Projects, bool Everything, string Scope);

/// <summary>
/// Chooses the projects to verify: named ones, the owners of changed paths plus their
/// direct dependents, <c>verify.projects.include</c>, or everything; then drops
/// <c>verify.projects.exclude</c> (docs/decisions/0017-plan-and-verify.md).
/// </summary>
public static class VerifySelector
{
    /// <param name="model">The workspace model.</param>
    /// <param name="projects">Resolved project paths given with <c>--projects</c>, or null.</param>
    /// <param name="affectedBy">Repository-relative changed paths given with <c>--affected-by</c>, or null.</param>
    /// <param name="config">The <c>verify.projects</c> section.</param>
    public static VerifySelection Select(WorkspaceModel model, IReadOnlyList<string>? projects, IReadOnlyList<string>? affectedBy, VerifyProjectsConfig config)
    {
        var all = model.Projects.Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        IReadOnlyList<string> chosen;
        string scope;
        if (projects is not null)
        {
            chosen = projects;
            scope = Count(projects.Count, "project") + " named with --projects";
        }
        else if (affectedBy is not null)
        {
            chosen = Affected(model, affectedBy);
            scope = string.Create(CultureInfo.InvariantCulture, $"{Count(chosen.Count, "project")} affected by {Count(affectedBy.Count, "path")}, with their direct dependents");
        }
        else if (config.Include.Count > 0)
        {
            var include = new PathGlobs(config.Include);
            chosen = [.. all.Where(include.Matches)];
            scope = Count(chosen.Count, "project") + " from verify.projects.include";
        }
        else
        {
            chosen = all;
            scope = "every project";
        }

        var exclude = new PathGlobs(config.Exclude);
        var kept = chosen.Where(p => !exclude.Matches(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (kept.Count < chosen.Count)
        {
            scope += string.Create(CultureInfo.InvariantCulture, $", less {Count(chosen.Count - kept.Count, "project")} in verify.projects.exclude");
        }

        return new VerifySelection(kept, kept.SequenceEqual(all, StringComparer.Ordinal), scope);
    }

    /// <summary>
    /// The projects owning each path, plus their direct dependents. A project file owns itself; a
    /// source file belongs to the projects compiling it; a .props, .targets, global.json, or
    /// nuget.config file affects every project beneath its folder; anything else belongs to the
    /// project whose folder holds it, or, when none does, to every project beneath it.
    /// </summary>
    public static List<string> Affected(WorkspaceModel model, IEnumerable<string> paths)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.Select(p => p.Replace('\\', '/').TrimEnd('/')))
        {
            owners.UnionWith(Owners(model, path));
        }

        var dependents = model.Graph.Edges.Where(e => owners.Contains(e.To)).Select(e => e.From);
        return [.. owners.Union(dependents, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Owners(WorkspaceModel model, string path)
    {
        var exact = model.Projects.Where(p => Same(p.Id, path)).Select(p => p.Id).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        var compiling = model.Projects.Where(p => p.Compile.Any(c => Same(c, path))).Select(p => p.Id).ToList();
        if (compiling.Count > 0)
        {
            return compiling;
        }

        if (IsBuildWide(path))
        {
            return Beneath(model, Folder(path));
        }

        var holding = model.Projects
            .Select(p => (p.Id, Folder: Folder(p.Id)))
            .Where(p => p.Folder.Length == 0 || path.StartsWith(p.Folder + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Folder.Length)
            .ToList();
        if (holding.Count > 0)
        {
            var deepest = holding[0].Folder.Length;
            return holding.TakeWhile(p => p.Folder.Length == deepest).Select(p => p.Id);
        }

        // Nobody's folder holds it: everything beneath it (a folder) or beneath its folder (a file).
        return Beneath(model, model.Projects.Any(p => p.Id.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) ? path : Folder(path));
    }

    /// <summary>Files MSBuild or NuGet pick up for every project beneath their folder.</summary>
    private static bool IsBuildWide(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Beneath(WorkspaceModel model, string folder) =>
        model.Projects.Where(p => folder.Length == 0 || p.Id.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id);

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Count(int value, string noun) =>
        value.ToString(CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? "" : "s");
}
