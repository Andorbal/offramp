using System.Text.Json.Nodes;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Usage;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Progress;
using Offramp.NuGet.Rules;

namespace Offramp.NuGet.Gac;

/// <summary>
/// <c>offramp deps gac</c>: .NET Framework assembly references and their modern
/// equivalents (rules/framework-assemblies.yml), with how much each is used, so an
/// unused reference is told apart from a real dependency.
/// </summary>
public static class GacAnalyzer
{
    public static GacResult Run(
        WorkspaceModel model, OfframpConfig config, string repositoryRoot, string? project, IProgressSink progress,
        DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var projects = model.Projects
            .Where(p => project is null || string.Equals(p.Id, project, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.AssemblyReferences.Any(r => r.Kind == AssemblyReferenceKind.Framework))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var results = new List<GacProject>();
        using var loader = new CompilationLoader(repositoryRoot);
        using (var phase = progress.BeginPhase("Counting framework assembly usage", 1, 1))
        {
            for (var i = 0; i < projects.Count; i++)
            {
                phase.Report(i, projects.Count, projects[i].Id);
                results.Add(Analyze(projects[i], loader, diagnostics, cancellationToken));
            }

            phase.Report(projects.Count, projects.Count);
        }

        var references = results.SelectMany(p => p.References).ToList();
        return new GacResult
        {
            Target = config.TargetFramework,
            Projects = results,
            Summary = new GacSummary(
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.Builtin),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.Package),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.CompatPack),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.None),
                references.Count(r => r.Mapping.Kind == FrameworkAssemblyKind.Unknown),
                references.Count(r => r.Usages == 0)),
        };
    }

    private static GacProject Analyze(ProjectInfo project, CompilationLoader loader, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var names = project.AssemblyReferences.Where(r => r.Kind == AssemblyReferenceKind.Framework).Select(r => r.Name).ToList();
        IReadOnlyDictionary<string, int>? usages = null;
        try
        {
            var compilation = loader.LoadForProject(project);
            usages = compilation is null ? null : AssemblyUsage.Count(compilation, names, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            diagnostics.Report(DiagnosticCatalog.OFR0132, $"The compilation of {project.Id} could not be rebuilt from the compiler log: {ex.Message}",
                new DiagnosticLocation(Project: project.Id),
                [KeyValuePair.Create<string, JsonNode?>("reason", ex.Message)]);
        }

        return new GacProject
        {
            Project = project.Id,
            TargetFrameworks = project.TargetFrameworks,
            References = [.. names
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(n => new GacReference
                {
                    Name = n,
                    Mapping = FrameworkAssemblyMap.Find(n),
                    Usages = usages is not null && usages.TryGetValue(n, out var count) ? count : null,
                })],
        };
    }
}
