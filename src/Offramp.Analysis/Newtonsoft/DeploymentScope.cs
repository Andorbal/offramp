using NuGet.Versioning;
using Offramp.Core.Model;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>Application deployments that determine which Newtonsoft.Json behavior a project runs under.</summary>
public sealed record DeploymentScope(string Project, IReadOnlyList<string> DeployedReleases, bool InScope, IReadOnlyList<string> DeployedBy);

/// <summary>Computes deployed release scope from application project-reference closures.</summary>
public static class NewtonsoftDeploymentScope
{
    private static readonly HashSet<ProjectKind> ApplicationKinds = [ProjectKind.Web, ProjectKind.Service, ProjectKind.Console, ProjectKind.Winforms, ProjectKind.Wpf];

    public static IReadOnlyDictionary<string, DeploymentScope> Compute(WorkspaceModel model, NuGetVersion target)
    {
        var projects = model.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var applications = model.Projects
            .Where(p => ApplicationKinds.Contains(p.Kind) && !IsTest(p)
                && (p.Kind != ProjectKind.Console || string.Equals(p.OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => (Project: p, Release: Release(p)))
            .Where(p => p.Release is not null)
            .ToList();
        var memberships = model.Projects.ToDictionary(p => p.Id, _ => new List<(string Application, NuGetVersion Release)>(), StringComparer.Ordinal);
        foreach (var (application, release) in applications)
        {
            foreach (var project in Closure(application))
            {
                if (memberships.TryGetValue(project, out var list))
                {
                    list.Add((application.Id, release!));
                }
            }
        }

        var result = new Dictionary<string, DeploymentScope>(StringComparer.Ordinal);
        foreach (var project in model.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var deployments = memberships[project.Id];
            if (deployments.Count == 0 && IsTest(project))
            {
                var referencedDeployments = project.ProjectReferences
                    .Where(memberships.ContainsKey)
                    .SelectMany(reference => memberships[reference])
                    .ToList();
                if (referencedDeployments.Count > 0)
                {
                    deployments = referencedDeployments;
                }
            }

            if (deployments.Count == 0 && Release(project) is { } own)
            {
                deployments = [(project.Id, own)];
            }

            var ordered = deployments.Select(d => d.Release).Distinct().OrderBy(v => v, VersionComparer.VersionRelease).ToList();
            var inScope = ordered.Any(version => version < target);
            result[project.Id] = new DeploymentScope(project.Id,
                [.. ordered.Select(v => v.ToNormalizedString())], inScope,
                [.. deployments.Select(d => d.Application).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        }

        return result;

        IEnumerable<string> Closure(ProjectInfo application)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(application.Id);
            while (pending.TryPop(out var id))
            {
                if (!visited.Add(id))
                {
                    continue;
                }

                yield return id;
                if (projects.TryGetValue(id, out var current))
                {
                    foreach (var reference in current.ProjectReferences.OrderByDescending(id => id, StringComparer.Ordinal))
                    {
                        pending.Push(reference);
                    }
                }
            }
        }

        NuGetVersion? Release(ProjectInfo project)
        {
            var tfm = project.TargetFrameworks
                .Where(target => target.StartsWith("net4", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault()
                ?? project.TargetFrameworks.Order(StringComparer.Ordinal).FirstOrDefault();
            return tfm is null || NewtonsoftReferences.For(project, tfm) is not { } reference ? null : NewtonsoftReferences.Release(reference);
        }
    }

    private static bool IsTest(ProjectInfo project) => project.IsTestProject || project.Kind == ProjectKind.Test;
}
