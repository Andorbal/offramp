using Offramp.Core.Caching;
using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Store;

/// <summary>What changed since the model was built.</summary>
public sealed record Staleness(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    bool SourceChanged)
{
    public bool IsStale => Changed.Count > 0 || Added.Count > 0 || Removed.Count > 0 || SourceChanged;

    public string Describe()
    {
        var parts = new List<string>();
        if (Changed.Count > 0) parts.Add($"{Changed.Count} changed ({string.Join(", ", Changed.Take(3))}{(Changed.Count > 3 ? ", …" : "")})");
        if (Added.Count > 0) parts.Add($"{Added.Count} added ({string.Join(", ", Added.Take(3))}{(Added.Count > 3 ? ", …" : "")})");
        if (Removed.Count > 0) parts.Add($"{Removed.Count} removed ({string.Join(", ", Removed.Take(3))}{(Removed.Count > 3 ? ", …" : "")})");
        if (SourceChanged) parts.Add("the log it was read from changed");
        return string.Join("; ", parts);
    }
}

/// <summary>
/// The files that shape the workspace model, hashed with SHA-256. Content hashes
/// rather than modification times, so a fresh clone of the same commit is not
/// stale (docs/decisions/0009-staleness-by-content-hash.md).
/// </summary>
public static class WorkspaceInputs
{
    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".sqlproj", ".sln", ".slnx",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "TestResults", "artifacts",
    };

    /// <summary>
    /// Hashes the inputs. Solution filters are included only when <paramref name="solution"/>
    /// is one (the model was built from it); other filters, such as those <c>slice</c>
    /// writes, do not make the model stale.
    /// </summary>
    public static IReadOnlyList<InputFile> Collect(string repositoryRoot, string stateDirectory, string? solution = null)
    {
        var state = Path.GetFullPath(stateDirectory);
        var inputs = new List<InputFile>();
        var pending = new Stack<string>([repositoryRoot]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files, subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory);
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var relative = RepoPaths.ToRepositoryRelative(repositoryRoot, file);
                if (IsInput(Path.GetFileName(file)) || string.Equals(relative, solution, StringComparison.Ordinal))
                {
                    inputs.Add(new InputFile(relative, ContentHash.Sha256File(file)));
                }
            }

            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name)
                    && !string.Equals(Path.GetFullPath(sub), state, StringComparison.Ordinal))
                {
                    pending.Push(sub);
                }
            }
        }

        return [.. inputs.OrderBy(i => i.Path, StringComparer.Ordinal)];
    }

    public static bool IsInput(string fileName) =>
        ProjectExtensions.Contains(Path.GetExtension(fileName))
        || fileName.Equals("packages.config", StringComparison.OrdinalIgnoreCase)
        || (fileName.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase)
            && (fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)));

    public static Staleness Compare(WorkspaceModel model, string repositoryRoot, string stateDirectory)
    {
        var current = Collect(repositoryRoot, stateDirectory, model.Solution).ToDictionary(i => i.Path, i => i.Sha256, StringComparer.Ordinal);
        var recorded = model.Inputs.ToDictionary(i => i.Path, i => i.Sha256, StringComparer.Ordinal);
        var changed = recorded.Where(r => current.TryGetValue(r.Key, out var hash) && hash != r.Value).Select(r => r.Key);
        var added = current.Keys.Where(k => !recorded.ContainsKey(k));
        var removed = recorded.Keys.Where(k => !current.ContainsKey(k));

        var sourceChanged = false;
        if (model.Source.Kind != WorkspaceSourceKind.Build)
        {
            var sourcePath = Path.GetFullPath(model.Source.Path, repositoryRoot);
            sourceChanged = File.Exists(sourcePath) && ContentHash.Sha256File(sourcePath) != model.Source.Sha256;
        }

        return new Staleness(
            [.. changed.Order(StringComparer.Ordinal)],
            [.. added.Order(StringComparer.Ordinal)],
            [.. removed.Order(StringComparer.Ordinal)],
            sourceChanged);
    }
}
