using System.Text.Json;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Offramp.Workspace.Ingest;

/// <summary>The projects a solution or solution filter lists.</summary>
public sealed record SolutionProjects(string SolutionFile, IReadOnlyList<string> ProjectPaths);

/// <summary>Reads .sln, .slnx (Microsoft.VisualStudio.SolutionPersistence), and .slnf (JSON).</summary>
public static class SolutionReader
{
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
            return new SolutionProjects(underlying, projects);
        }

        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
            ?? throw new InvalidDataException($"'{solutionPath}' is not a recognized solution file.");
        var model = await serializer.OpenAsync(solutionPath, cancellationToken);
        var directory = Path.GetDirectoryName(solutionPath)!;
        var paths = model.SolutionProjects
            .Select(p => Path.GetFullPath(p.FilePath.Replace('\\', Path.DirectorySeparatorChar), directory))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new SolutionProjects(solutionPath, paths);
    }
}
