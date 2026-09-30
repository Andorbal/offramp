using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Verification;

/// <summary>
/// The working tree's files that a recorded build read, which a scratch copy of the repository needs
/// to build as the working tree does (docs/decisions/0062-the-scratch-copy-has-what-the-build-read.md).
/// </summary>
public static class BuildReads
{
    /// <summary>
    /// The files inside the repository that the build recorded in <paramref name="binlogPath"/> read
    /// (<see cref="BinlogReader.ReadInputs"/>: imports, file items, reference hint paths, copy sources, and the
    /// assemblies its tasks were loaded from with the files beside them, which they load their dependencies from)
    /// and that exist in the working tree, repository-relative and sorted. Never a file in a <c>bin</c> or
    /// <c>obj</c> folder (the build writes those), in <c>.git</c>, or in the state directory; from a restored
    /// <c>packages</c> folder, only the files the build read. Empty when the log is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<string> Read(string binlogPath, string repositoryRoot, string stateDirectory)
    {
        if (!File.Exists(binlogPath))
        {
            return [];
        }

        BuildInputs inputs;
        try
        {
            inputs = BinlogReader.ReadInputs(binlogPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }

        var mapper = CapturePathMapper.Infer(repositoryRoot, inputs.ProjectFiles);
        var state = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var besideTasks = inputs.TaskAssemblies
            .Select(mapper.ToRelative)
            .OfType<string>()
            .Where(IsCopyable)
            .SelectMany(assembly => FilesBeside(repositoryRoot, assembly));
        return
        [
            .. inputs.Files
                .Select(mapper.ToRelative)
                .OfType<string>()
                .Concat(besideTasks)
                .Where(IsCopyable)
                .Select(f => (Relative: f, Absolute: RepoPaths.ToAbsolute(repositoryRoot, f)))
                .Where(f => !Path.GetFullPath(f.Absolute).StartsWith(state, StringComparison.Ordinal) && File.Exists(f.Absolute))
                .Select(f => f.Relative)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>The files in the folder of a repository-relative file (itself included), repository-relative.</summary>
    private static IEnumerable<string> FilesBeside(string repositoryRoot, string relative)
    {
        var folder = RepoPaths.ToAbsolute(repositoryRoot, Path.GetDirectoryName(relative) ?? "");
        try
        {
            return [.. Directory.EnumerateFiles(folder).Select(f => RepoPaths.ToRepositoryRelative(repositoryRoot, f))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A repository-relative file a scratch copy may take: not the build's output (<c>bin</c>, <c>obj</c>) or git's.</summary>
    private static bool IsCopyable(string relative) =>
        relative.Length > 0
        && !relative.Split('/').Any(s => s is "." or ".."
            || s.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || s.Equals(".git", StringComparison.OrdinalIgnoreCase));
}
