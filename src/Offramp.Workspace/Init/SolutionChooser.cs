using System.Globalization;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Init;

/// <summary>
/// The solution <c>init</c> and <c>scan</c> work on when none is configured; <see cref="Reason"/> says why when
/// it was chosen among several, and <see cref="Tie"/> describes the candidates when none was.
/// </summary>
public sealed record SolutionChoice(string? Solution, string? Reason = null, string? Tie = null);

/// <summary>
/// Chooses among a repository's solutions (docs/decisions/0050-choose-among-several-solutions.md): the only one; else
/// the only one at the root; else, setting aside solutions with a Web Site project (which .NET's MSBuild cannot build,
/// MSB4249) when others have none, the one that contains every other one's projects, else the one with the most
/// projects. A tie chooses nothing. Solution filters are never chosen.
/// </summary>
public static class SolutionChooser
{
    /// <summary>The project type of a folder-based ASP.NET Web Site project.</summary>
    public static readonly Guid WebSiteProjectType = new("E24C65DC-7377-472B-9ABA-BC803B73C61A");

    private sealed record Candidate(string Path, IReadOnlySet<string> Projects, int WebSites);

    public static async Task<SolutionChoice> ChooseAsync(string repositoryRoot, IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        var solutions = candidates.Where(IsSolutionFile).Order(StringComparer.Ordinal).ToList();
        if (solutions.Count <= 1)
        {
            return new SolutionChoice(solutions.FirstOrDefault());
        }

        var atRoot = solutions.Where(s => !s.Contains('/', StringComparison.Ordinal)).ToList();
        if (atRoot.Count == 1)
        {
            return new SolutionChoice(atRoot[0]);
        }

        var read = new List<Candidate>();
        foreach (var solution in solutions)
        {
            if (await ReadAsync(repositoryRoot, solution, cancellationToken) is { } candidate)
            {
                read.Add(candidate);
            }
        }

        return Choose(read, solutions);
    }

    private static SolutionChoice Choose(List<Candidate> read, IReadOnlyList<string> solutions)
    {
        var unreadable = solutions.Where(s => read.All(c => c.Path != s)).ToList();
        var notes = new List<string>();
        if (unreadable.Count > 0)
        {
            notes.Add($"{string.Join(", ", unreadable)} could not be read");
        }

        var setAside = read.Any(c => c.WebSites == 0) ? read.Where(c => c.WebSites > 0).ToList() : [];
        if (setAside.Count > 0)
        {
            notes.Add($"{string.Join(", ", setAside.Select(c => c.Path))} {(setAside.Count == 1 ? "has" : "have")} a Web Site project, which .NET's MSBuild cannot build (MSB4249)");
        }

        var remaining = read.Except(setAside).ToList();
        if (remaining.Count == 0)
        {
            return new SolutionChoice(null, Tie: string.Join("; ", notes));
        }

        if (remaining.Count == 1)
        {
            return new SolutionChoice(remaining[0].Path, string.Join("; ", notes));
        }

        var supersets = remaining.Where(c => remaining.All(o => o.Projects.IsSubsetOf(c.Projects))).ToList();
        if (supersets.Count == 1)
        {
            var others = remaining.Where(c => c != supersets[0]).Select(c => c.Path);
            notes.Insert(0, $"it contains every project of {string.Join(", ", others)}");
            return new SolutionChoice(supersets[0].Path, string.Join("; ", notes));
        }

        var ranked = remaining.OrderByDescending(c => c.Projects.Count).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
        if (ranked[0].Projects.Count > ranked[1].Projects.Count)
        {
            notes.Insert(0, string.Create(CultureInfo.InvariantCulture,
                $"it has the most projects ({ranked[0].Projects.Count}; next: {ranked[1].Path} with {ranked[1].Projects.Count})"));
            return new SolutionChoice(ranked[0].Path, string.Join("; ", notes));
        }

        notes.Insert(0, string.Join(", ", ranked.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Path} has {c.Projects.Count} projects"))));
        return new SolutionChoice(null, Tie: string.Join("; ", notes));
    }

    /// <summary>A solution's projects (absolute paths) and how many are Web Site projects; null when it cannot be read.</summary>
    private static async Task<Candidate?> ReadAsync(string repositoryRoot, string solution, CancellationToken cancellationToken)
    {
        var path = RepoPaths.ToAbsolute(repositoryRoot, solution);
        try
        {
            if (SolutionSerializers.GetSerializerByMoniker(path) is not { } serializer)
            {
                return null;
            }

            var model = await serializer.OpenAsync(path, cancellationToken);
            var directory = Path.GetDirectoryName(path)!;
            var projects = model.SolutionProjects
                .Select(p => Path.GetFullPath(p.FilePath.Replace('\\', Path.DirectorySeparatorChar), directory).TrimEnd(Path.DirectorySeparatorChar))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new Candidate(solution, projects, model.SolutionProjects.Count(p => p.TypeId == WebSiteProjectType));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public static bool IsSolutionFile(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}
