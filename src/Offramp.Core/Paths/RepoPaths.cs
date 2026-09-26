namespace Offramp.Core.Paths;

/// <summary>
/// Path conventions for output: repository-relative, forward slashes, never
/// absolute (the repository root appears once, in the envelope).
/// </summary>
public static class RepoPaths
{
    /// <summary>Converts an absolute path to a repository-relative path with <c>/</c> separators.</summary>
    public static string ToRepositoryRelative(string repositoryRoot, string absolutePath)
    {
        var relative = Path.GetRelativePath(repositoryRoot, absolutePath);
        return Normalize(relative);
    }

    /// <summary>Uses <c>/</c> separators and drops a leading <c>./</c>.</summary>
    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized == "." ? "" : normalized;
    }

    /// <summary>Resolves a repository-relative path (either separator) to an absolute path.</summary>
    public static string ToAbsolute(string repositoryRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// An absolute path with every symbolic link along it resolved (macOS's <c>/var</c> is
    /// <c>/private/var</c>): the form tools report after asking the operating system for the
    /// current directory. Parts that do not exist yet are kept as given.
    /// </summary>
    public static string Canonical(string path) => Canonical(path, depth: 0);

    private static string Canonical(string path, int depth)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);

            // A link's target can itself pass through links; 40 levels is the usual loop limit.
            if (depth < 40 && info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = Canonical(target.FullName, depth + 1);
            }
        }

        return current;
    }

    /// <summary>Ordinal comparison for repository-relative paths, used to sort every path list.</summary>
    public static StringComparer Comparer => StringComparer.Ordinal;
}
