using System.Text;
using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Slicing;

public sealed record SliceCounts(int Requested, int Dependencies, int Dependents, int Tests, int Total);

/// <summary>The <c>result</c> of <c>offramp slice</c> (<c>schemas/v1/slice.json</c>).</summary>
public sealed record SliceResult
{
    /// <summary>The projects asked for, repository-relative.</summary>
    public required IReadOnlyList<string> For { get; init; }

    /// <summary>The solution the filter points at, repository-relative.</summary>
    public required string Solution { get; init; }

    /// <summary>Every project in the slice, repository-relative, sorted.</summary>
    public required IReadOnlyList<string> Projects { get; init; }

    public required SliceCounts Counts { get; init; }

    /// <summary><c>slnf</c> or <c>slngen</c>.</summary>
    public required string Format { get; init; }

    /// <summary>Repository-relative path written, or null when printed only.</summary>
    public string? Output { get; init; }

    /// <summary>The solution filter JSON, or the SlnGen command line.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// Solution filters for a project closure (docs/spec/commands/workspace.md#slice):
/// the projects, their transitive dependencies, optionally their transitive
/// dependents and the test projects that reference anything in the slice.
/// </summary>
public static class SliceBuilder
{
    public static SliceResult Build(
        WorkspaceModel model, IReadOnlyList<string> requested, bool includeDependents, bool includeTests,
        string format, string solution, string? outputRelative)
    {
        var dependencies = Adjacency(model, reverse: false);
        var dependents = Adjacency(model, reverse: true);

        var closure = Reach(requested, dependencies);
        var dependencyCount = closure.Count - requested.Count;
        var dependentCount = 0;
        if (includeDependents)
        {
            var up = Reach(requested, dependents);
            up.ExceptWith(closure);
            dependentCount = up.Count;
            closure.UnionWith(Reach(up, dependencies));
        }

        var testCount = 0;
        if (includeTests)
        {
            var tests = model.Projects
                .Where(p => p.Kind == ProjectKind.Test && !closure.Contains(p.Id))
                .Where(p => dependencies.GetValueOrDefault(p.Id)?.Any(closure.Contains) == true)
                .Select(p => p.Id)
                .ToList();
            testCount = tests.Count;
            closure.UnionWith(Reach(tests, dependencies));
        }

        var projects = closure.Order(StringComparer.Ordinal).ToList();
        var content = format == "slngen"
            ? SlnGenCommand(projects, outputRelative)
            : SolutionFilter(solution, projects, outputRelative);
        return new SliceResult
        {
            For = requested,
            Solution = solution,
            Projects = projects,
            Counts = new SliceCounts(requested.Count, dependencyCount, dependentCount, testCount, projects.Count),
            Format = format,
            Output = outputRelative,
            Content = content,
        };
    }

    /// <summary>
    /// A .slnf: the solution path relative to the filter file, and project paths
    /// relative to the solution with backslashes, as Visual Studio writes them.
    /// </summary>
    public static string SolutionFilter(string solution, IReadOnlyList<string> projects, string? outputRelative)
    {
        var filterDirectory = outputRelative is null ? "" : Path.GetDirectoryName(outputRelative.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
        var solutionPath = Relative(filterDirectory, solution).Replace('/', '\\');
        var solutionDirectory = Path.GetDirectoryName(solution.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
        var builder = new StringBuilder();
        builder.Append("{\n  \"solution\": {\n    \"path\": ").Append(Json(solutionPath)).Append(",\n    \"projects\": [\n");
        for (var i = 0; i < projects.Count; i++)
        {
            builder.Append("      ").Append(Json(Relative(solutionDirectory, projects[i]).Replace('/', '\\')));
            builder.Append(i < projects.Count - 1 ? ",\n" : "\n");
        }

        builder.Append("    ]\n  }\n}\n");
        return builder.ToString();
    }

    private static string SlnGenCommand(IReadOnlyList<string> projects, string? outputRelative)
    {
        var solutionFile = outputRelative is null ? "slice.sln" : Path.ChangeExtension(outputRelative, ".sln");
        return "slngen --launch false --solutionfile " + Quote(solutionFile) + " " + string.Join(' ', projects.Select(Quote)) + "\n";
    }

    private static Dictionary<string, List<string>> Adjacency(WorkspaceModel model, bool reverse) =>
        model.Graph.Edges
            .GroupBy(e => reverse ? e.To : e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => reverse ? e.From : e.To).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

    private static HashSet<string> Reach(IEnumerable<string> start, Dictionary<string, List<string>> adjacency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(start);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            foreach (var next in adjacency.GetValueOrDefault(node) ?? [])
            {
                pending.Push(next);
            }
        }

        return seen;
    }

    /// <summary>A path from <paramref name="fromDirectory"/> to <paramref name="to"/>, both repository-relative.</summary>
    private static string Relative(string fromDirectory, string to)
    {
        var relative = Path.GetRelativePath("/r/" + fromDirectory, "/r/" + to);
        return RepoPaths.Normalize(relative);
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static string Json(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
