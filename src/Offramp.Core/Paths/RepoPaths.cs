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

    /// <summary>Ordinal comparison for repository-relative paths, used to sort every path list.</summary>
    public static StringComparer Comparer => StringComparer.Ordinal;
}
