using System.Text.Json;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Offramp.Workspace.Ingest;

/// <summary>The projects a solution or solution filter lists.</summary>
public sealed record SolutionProjects(string SolutionFile, IReadOnlyList<string> ProjectPaths)
{
    /// <summary>
    /// The listed ASP.NET Web Site projects: folders without a project file, which only .NET Framework's MSBuild
    /// builds (absolute, as in <see cref="ProjectPaths"/>, sorted).
    /// </summary>
    public IReadOnlyList<string> WebSites { get; init; } = [];
}

/// <summary>Reads .sln, .slnx (Microsoft.VisualStudio.SolutionPersistence), and .slnf (JSON).</summary>
public static class SolutionReader
{
    /// <summary>The project type of an ASP.NET Web Site project in a solution.</summary>
    public static readonly Guid WebSiteType = new("E24C65DC-7377-472B-9ABA-BC803B73C61A");

    /// <summary>
    /// Absolute paths of the projects in the solution (for a filter, only the
    /// filtered projects); the solution file itself for filters is the underlying one.
    /// </summary>
    public static async Task<SolutionProjects> ReadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        if (solutionPath.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(solutionPath, cancellationToken));
            var solution = document.RootElement.GetProperty("solution");
            var underlying = Path.GetFullPath(
                solution.GetProperty("path").GetString()!.Replace('\\', Path.DirectorySeparatorChar),
                Path.GetDirectoryName(solutionPath)!);
            var solutionDirectory = Path.GetDirectoryName(underlying)!;
            var projects = solution.GetProperty("projects").EnumerateArray()
                .Select(p => Path.GetFullPath(p.GetString()!.Replace('\\', Path.DirectorySeparatorChar), solutionDirectory))
                .Order(StringComparer.Ordinal)
                .ToList();
            var listed = projects.ToHashSet(StringComparer.Ordinal);
            var webSites = await TryOpenAsync(underlying, cancellationToken) is { } model
                ? WebSitesOf(model, solutionDirectory).Where(listed.Contains).ToList()
                : [];
            return new SolutionProjects(underlying, projects) { WebSites = webSites };
        }

        var directory = Path.GetDirectoryName(solutionPath)!;
        var opened = await OpenAsync(solutionPath, cancellationToken);
        var paths = opened.SolutionProjects
            .Select(p => FullPath(p, directory))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new SolutionProjects(solutionPath, paths) { WebSites = WebSitesOf(opened, directory) };
    }

    private static async Task<SolutionModel> OpenAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
            ?? throw new InvalidDataException($"'{solutionPath}' is not a recognized solution file.");
        return await serializer.OpenAsync(solutionPath, cancellationToken);
    }

    /// <summary>The solution a filter names, or null when it cannot be read: the filter's own list still stands.</summary>
    private static async Task<SolutionModel?> TryOpenAsync(string solutionPath, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(solutionPath) ? await OpenAsync(solutionPath, cancellationToken) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static List<string> WebSitesOf(SolutionModel model, string directory) =>
        [.. model.SolutionProjects.Where(p => p.TypeId == WebSiteType).Select(p => FullPath(p, directory)).Order(StringComparer.Ordinal)];

    private static string FullPath(SolutionProjectModel project, string directory) =>
        Path.GetFullPath(project.FilePath.Replace('\\', Path.DirectorySeparatorChar), directory);
}
