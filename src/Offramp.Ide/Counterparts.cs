using System.Text.RegularExpressions;
using NuGet.Frameworks;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Ide;

/// <summary>
/// Each .NET Framework-only project's counterparts (docs/spec/commands/ide.md#counterparts-and-the-project-map):
/// the project map (the editor's entries, then <c>offramp.yml</c>'s), else the portable projects
/// it references directly. A counterpart must be able to take the project's code.
/// </summary>
public static class Counterparts
{
    public const string FromMap = "map";
    public const string FromReferences = "referenced";
    public const string None = "none";

    public static IReadOnlyList<ProjectCounterparts> Resolve(WorkspaceModel model, OfframpConfig config, IdeSettings settings, DiagnosticBag diagnostics)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        void Report(DiagnosticDescriptor descriptor, string message, string? project)
        {
            if (reported.Add(descriptor.Code + " " + message))
            {
                diagnostics.Report(descriptor, message, project is null ? null : new DiagnosticLocation(project));
            }
        }

        var entries = settings.ProjectMap.Select(e => (Entry: e, Origin: "the editor's offramp.projectMap"))
            .Concat(config.ProjectMap.Select(e => (Entry: e, Origin: "offramp.yml")))
            .ToList();
        foreach (var (entry, origin) in entries)
        {
            if (!IsPattern(entry.From) && Find(model, entry.From) is null)
            {
                Report(DiagnosticCatalog.OFR6002, Unresolved(model, entry.From, $"projectMap entry {Describe(entry)} ({origin})"), null);
            }

            if (!entry.To.Contains("{name}", StringComparison.Ordinal) && Find(model, entry.To) is null)
            {
                Report(DiagnosticCatalog.OFR6002, Unresolved(model, entry.To, $"projectMap entry {Describe(entry)} ({origin})"), null);
            }
        }

        var implicitCounterparts = settings.ImplicitCounterparts ?? config.Ide.ImplicitCounterparts;
        var result = new List<ProjectCounterparts>();
        foreach (var project in model.Projects.Where(p => Applies(p)).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var mapped = new List<string>();
            var matched = false;
            foreach (var (entry, origin) in entries.Where(e => Matches(model, e.Entry.From, project)))
            {
                matched = true;
                var name = entry.To.Replace("{name}", project.Name, StringComparison.Ordinal);
                if (Find(model, name) is not { } counterpart)
                {
                    continue; // a convention's project that does not exist, or an exact one already reported
                }

                if (counterpart.Id == project.Id)
                {
                    Report(DiagnosticCatalog.OFR6002, $"projectMap entry {Describe(entry)} ({origin}) maps {project.Id} to itself.", project.Id);
                    continue;
                }

                if (Problem(model, config, project, counterpart) is { } problem)
                {
                    Report(DiagnosticCatalog.OFR6004, $"{counterpart.Id} cannot take code from {project.Id}: {problem}.", project.Id);
                    continue;
                }

                if (!mapped.Contains(counterpart.Id, StringComparer.Ordinal))
                {
                    mapped.Add(counterpart.Id);
                }
            }

            if (matched)
            {
                result.Add(new ProjectCounterparts { Project = project.Id, Counterparts = mapped, Source = FromMap });
                continue;
            }

            var referenced = implicitCounterparts && !project.IsTestProject && project.Kind != ProjectKind.Test
                ? project.ProjectReferences
                    .Select(r => model.Projects.FirstOrDefault(p => p.Id == r))
                    .OfType<ProjectInfo>()
                    .Where(p => p.FrameworkClass is FrameworkClass.Standard or FrameworkClass.Dual && !p.IsTestProject && p.Kind != ProjectKind.Test)
                    .Where(p => Problem(model, config, project, p) is null)
                    .Select(p => p.Id)
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
            result.Add(new ProjectCounterparts { Project = project.Id, Counterparts = referenced, Source = referenced.Count > 0 ? FromReferences : None });
        }

        return result;
    }

    /// <summary>A C# project the editor reports on: .NET Framework-only and not excluded.</summary>
    public static bool Applies(ProjectInfo project) =>
        project.Language == "csharp" && project.FrameworkClass == FrameworkClass.Framework && !project.Config.Excluded;

    /// <summary>Why a counterpart cannot take a project's code, or null when it can.</summary>
    public static string? Problem(WorkspaceModel model, OfframpConfig config, ProjectInfo project, ProjectInfo counterpart)
    {
        if (counterpart.Language != "csharp")
        {
            return "it is not a C# project";
        }

        if (counterpart.Config.Excluded)
        {
            return "paths.exclude excludes it";
        }

        if (config.Projects.Any(p => p.Frozen && new PathGlobs([p.Path]).Matches(counterpart.Id)))
        {
            return "it is frozen in offramp.yml";
        }

        switch (counterpart.FrameworkClass)
        {
            case FrameworkClass.Framework:
                return "it is .NET Framework-only";
            case FrameworkClass.Modern:
                return "it targets modern .NET only";
        }

        if (counterpart.CompilerCalls.Count == 0)
        {
            return "the scan recorded no compilation for it";
        }

        if (Reach(model, counterpart.Id).Contains(project.Id))
        {
            return $"it depends on {project.Id}";
        }

        var available = counterpart.CompilerCalls.Keys.Concat(counterpart.TargetFrameworks).Distinct(StringComparer.Ordinal).ToList();
        foreach (var tfm in project.CompilerCalls.Keys.Concat(project.TargetFrameworks).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!CanReference(tfm, available))
            {
                return $"{project.Id} cannot reference it for {tfm}";
            }
        }

        return null;
    }

    /// <summary>The project a path (repository-relative) or unique name names, or null.</summary>
    public static ProjectInfo? Find(WorkspaceModel model, string value)
    {
        var path = value.Replace('\\', '/').TrimStart('.', '/');
        var byPath = model.Projects.Where(p => string.Equals(p.Id, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPath.Count > 0)
        {
            return byPath.FirstOrDefault(p => p.Id == path) ?? byPath[0];
        }

        var byName = model.Projects.Where(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static string Unresolved(WorkspaceModel model, string value, string where) =>
        model.Projects.Count(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{where}: '{value}' is the name of {model.Projects.Count(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase))} projects; use a path."
            : $"{where}: '{value}' is not a project of the workspace model.";

    private static bool IsPattern(string value) => value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);

    private static bool Matches(WorkspaceModel model, string from, ProjectInfo project)
    {
        if (!IsPattern(from))
        {
            return Find(model, from)?.Id == project.Id;
        }

        var pattern = "^" + Regex.Escape(from.Replace('\\', '/')).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        var subject = from.Contains('/', StringComparison.Ordinal) ? project.Id : project.Name;
        return Regex.IsMatch(subject, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string Describe(ProjectMapEntry entry) => $"{entry.From} → {entry.To}";

    private static bool CanReference(string tfm, IReadOnlyList<string> available)
    {
        var framework = NuGetFramework.Parse(tfm);
        var candidates = available.Select(a => new Candidate(NuGetFramework.Parse(a))).ToList();
        return NuGetFrameworkUtility.GetNearest(candidates, framework, c => c.Framework) is not null;
    }

    private sealed record Candidate(NuGetFramework Framework);

    /// <summary>Every project a project depends on, directly or not.</summary>
    public static HashSet<string> Reach(WorkspaceModel model, string project)
    {
        var dependencies = model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(dependencies[project]);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (seen.Add(next))
            {
                foreach (var further in dependencies[next])
                {
                    pending.Push(further);
                }
            }
        }

        return seen;
    }
}
