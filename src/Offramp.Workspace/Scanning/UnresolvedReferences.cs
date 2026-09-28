using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;

namespace Offramp.Workspace.Scanning;

/// <summary>
/// Project references the graph cannot follow (<c>docs/spec/02-workspace-model.md</c>): a
/// reference to a project that is not in the model is recorded on the referencing project as an
/// <see cref="UnresolvedReference"/> and reported as OFR0105, instead of being dropped.
/// </summary>
public static class UnresolvedReferences
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".vbproj", ".fsproj" };

    /// <param name="projects">The loaded projects.</param>
    /// <param name="declaredReferences">Per project id, every <c>ProjectReference</c> it declares (<see cref="Model.ProjectModelBuilder.DeclaredProjectReferences"/>).</param>
    /// <param name="notLoaded">Projects the solution lists that did not load, with the reason.</param>
    /// <param name="diagnostics">Where OFR0105 is reported.</param>
    /// <param name="loading">The model's loading diagnostics, appended to.</param>
    /// <returns>The projects, with <see cref="ProjectInfo.UnresolvedReferences"/> filled in.</returns>
    public static List<ProjectInfo> Resolve(
        IReadOnlyList<ProjectInfo> projects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredReferences,
        IReadOnlyList<NotLoadedProject> notLoaded,
        DiagnosticBag diagnostics,
        List<Diagnostic> loading)
    {
        var ids = projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var reasons = notLoaded
            .GroupBy(n => n.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.OrdinalIgnoreCase);

        var result = new List<ProjectInfo>(projects.Count);
        foreach (var project in projects)
        {
            var unresolved = (declaredReferences.GetValueOrDefault(project.Id) ?? [])
                .Where(path => !ids.Contains(path))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(path => new UnresolvedReference { Path = path, Reason = ReasonFor(path, reasons) })
                .ToList();
            foreach (var reference in unresolved)
            {
                var reported = diagnostics.Report(DiagnosticCatalog.OFR0105,
                    $"Depends on {reference.Path}, which is not in the model: {reference.Reason}.",
                    new DiagnosticLocation(Project: project.Id),
                    [KeyValuePair.Create<string, JsonNode?>("reference", reference.Path), KeyValuePair.Create<string, JsonNode?>("reason", reference.Reason)]);
                if (reported is not null)
                {
                    loading.Add(reported);
                }
            }

            result.Add(unresolved.Count == 0 ? project : project with { UnresolvedReferences = unresolved });
        }

        return result;
    }

    private static string ReasonFor(string path, Dictionary<string, string> notLoaded)
    {
        if (notLoaded.TryGetValue(path, out var reason))
        {
            return reason;
        }

        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension)
            ? "not in the build log (outside the solution or slice that was scanned)"
            : $"unsupported project type ({extension.ToLowerInvariant()})";
    }
}
