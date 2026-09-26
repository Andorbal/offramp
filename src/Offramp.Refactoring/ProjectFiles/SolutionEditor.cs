using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Offramp.Core.Paths;

namespace Offramp.Refactoring.ProjectFiles;

/// <summary>Adds a project to a solution (.sln, .slnx) or a solution filter (.slnf), returning the new bytes.</summary>
public static class SolutionEditor
{
    /// <summary>The edits adding <paramref name="project"/>: the solution, and for a filter also the solution it filters.</summary>
    public static async Task<IReadOnlyList<(string Path, byte[] Before, byte[] After)>> AddProjectAsync(
        string repositoryRoot, string solution, string project, CancellationToken cancellationToken)
    {
        if (!solution.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return [await AddToSolutionAsync(repositoryRoot, solution, project, cancellationToken)];
        }

        var filterPath = RepoPaths.ToAbsolute(repositoryRoot, solution);
        var before = await File.ReadAllBytesAsync(filterPath, cancellationToken);
        var filter = JsonNode.Parse(before)!;
        var inner = filter["solution"]!["path"]!.GetValue<string>();
        var underlying = RepoPaths.ToRepositoryRelative(repositoryRoot,
            Path.GetFullPath(inner.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(filterPath)!));
        var solutionDirectory = Path.GetDirectoryName(RepoPaths.ToAbsolute(repositoryRoot, underlying))!;
        var entry = Path.GetRelativePath(solutionDirectory, RepoPaths.ToAbsolute(repositoryRoot, project)).Replace('/', '\\');
        var projects = filter["solution"]!["projects"]!.AsArray();
        if (!projects.Any(p => string.Equals(p!.GetValue<string>(), entry, StringComparison.OrdinalIgnoreCase)))
        {
            projects.Add(entry);
        }

        var after = new UTF8Encoding(false).GetBytes(filter.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        return [await AddToSolutionAsync(repositoryRoot, underlying, project, cancellationToken), (solution, before, after)];
    }

    private static async Task<(string, byte[], byte[])> AddToSolutionAsync(string repositoryRoot, string solution, string project, CancellationToken cancellationToken)
    {
        var path = RepoPaths.ToAbsolute(repositoryRoot, solution);
        var before = await File.ReadAllBytesAsync(path, cancellationToken);
        var serializer = SolutionSerializers.GetSerializerByMoniker(path)
            ?? throw new InvalidDataException($"'{solution}' is not a recognized solution file.");
        var model = await serializer.OpenAsync(path, cancellationToken);
        var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, RepoPaths.ToAbsolute(repositoryRoot, project)).Replace('\\', '/');
        if (model.SolutionProjects.Any(p => string.Equals(p.FilePath.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase)))
        {
            return (solution, before, before);
        }

        // Put it in the solution folder of the project's siblings, if they share one.
        var siblingFolder = model.SolutionProjects
            .Where(p => string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(p.FilePath.Replace('\\', '/'))), Path.GetDirectoryName(Path.GetDirectoryName(relative)), StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Parent)
            .FirstOrDefault(f => f is not null);
        model.AddProject(relative, projectTypeName: null, folder: siblingFolder);

        // The serializer writes files, not streams; write a temporary copy and read it back.
        var temporary = Path.Combine(Path.GetTempPath(), "offramp-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try
        {
            await serializer.SaveAsync(temporary, model, cancellationToken);
            return (solution, before, await File.ReadAllBytesAsync(temporary, cancellationToken));
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
