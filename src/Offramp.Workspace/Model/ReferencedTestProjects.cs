using Offramp.Core.Model;

namespace Offramp.Workspace.Model;

/// <summary>
/// Test projects that applications use (ADR 0074). What an application references, directly or through other
/// projects, ships with it, so framework-only test evidence and <c>IsTestProject=true</c> from
/// Microsoft.NET.Test.Sdk can be demoted to the project's other evidence. The test ProjectTypeGuid and
/// <c>offramp.yml</c> override remain explicit test declarations. A library or console application that references
/// a test project proves nothing: a library that gets its framework through the reference is a test project
/// nothing marks (<c>dead-code-evidence</c>'s <c>Specs</c>), and a console application may be a test runner.
/// </summary>
public static class ReferencedTestProjects
{
    public static (IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<(ProjectInfo Project, string Application)> Kept) Apply(
        IReadOnlyList<ProjectInfo> projects, IReadOnlyDictionary<string, ProjectFacts> facts)
    {
        var usedBy = UsedBy(projects);
        var result = projects.Select(p =>
        {
            if (!Demotable(p, facts) || !usedBy.TryGetValue(p.Id, out var application))
            {
                return p;
            }

            var (kind, evidence) = ProjectKindDetector.Detect(facts[p.Id] with { IsTestProject = false }, frameworks: false);
            return p with
            {
                Kind = kind,
                IsTestProject = false,
                HostsTests = ProjectKindDetector.TestFramework(facts[p.Id]) is not null,
                KindEvidence = $"{evidence} ({p.KindEvidence} ignored: the application {application} uses it)",
            };
        }).ToList();

        var kept = result
            .Where(p => IsTest(p) && usedBy.ContainsKey(p.Id))
            .Select(p => (p, usedBy[p.Id]))
            .ToList();
        return (result, kept);
    }

    /// <summary>Every project an application references, directly or through other projects, with the ordinal-first such application.</summary>
    private static Dictionary<string, string> UsedBy(IReadOnlyList<ProjectInfo> projects)
    {
        var byId = projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var usedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var application in projects.Where(IsApplication).Select(p => p.Id).Order(StringComparer.Ordinal))
        {
            var pending = new Stack<string>(byId[application].ProjectReferences);
            var seen = new HashSet<string>(StringComparer.Ordinal) { application };
            while (pending.TryPop(out var id))
            {
                if (!seen.Add(id) || !byId.TryGetValue(id, out var referenced))
                {
                    continue;
                }

                usedBy.TryAdd(id, application);
                foreach (var next in referenced.ProjectReferences)
                {
                    pending.Push(next);
                }
            }
        }

        return usedBy;
    }

    private static bool IsApplication(ProjectInfo project) =>
        !IsTest(project) && project.Kind is ProjectKind.Web or ProjectKind.Service or ProjectKind.Winforms or ProjectKind.Wpf;

    /// <summary>A test project only by a framework it references, with the facts to say what else it is.</summary>
    private static bool Demotable(ProjectInfo project, IReadOnlyDictionary<string, ProjectFacts> facts) =>
        project.Kind == ProjectKind.Test && project.Config.KindOverride is null
        && project.KindEvidence is { } evidence
        && (ProjectKindDetector.IsFrameworkEvidence(evidence) || evidence == "IsTestProject=true")
        && facts.TryGetValue(project.Id, out var projectFacts)
        && !HasTestProjectGuid(projectFacts);

    private static bool HasTestProjectGuid(ProjectFacts facts) =>
        facts.ProjectTypeGuids?.Contains("3AC096D0-A1C2-E12C-1390-A8335801FDAB", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasTestFramework(ProjectInfo project) =>
        project.PackageReferences.Any(p => ProjectKindDetector.TestPackages.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
        || (project.PackagesConfigPackages ?? []).Any(p => ProjectKindDetector.TestPackages.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
        || project.AssemblyReferences.Any(a => ProjectKindDetector.TestAssemblies.Contains(a.Name, StringComparer.OrdinalIgnoreCase));

    private static bool IsTest(ProjectInfo project) => project.IsTestProject || project.Kind == ProjectKind.Test;
}
