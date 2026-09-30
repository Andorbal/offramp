using System.Text.Json.Nodes;

namespace Offramp.Corpus.Tests.Harness;

/// <summary>
/// The fix <c>OFR0117</c> prescribes for a path spelled in another letter case than the file on disk, applied as a
/// user on Linux would apply it without editing the codebase: a symbolic link under the spelling the project uses.
/// Windows and macOS file systems ignore case, so there is nothing to do there.
/// </summary>
public static class CaseLinks
{
    /// <summary>
    /// Links every path in <c>data.paths</c> of the <c>OFR0117</c> diagnostics of <paramref name="scan"/> to the
    /// file or folder that exists under another letter case; returns the repository-relative paths it linked.
    /// </summary>
    public static IReadOnlyList<string> Apply(OfframpRun scan, string repository)
    {
        var linked = new List<string>();
        if (!OperatingSystem.IsLinux())
        {
            return linked;
        }

        var spelled = scan.Diagnostics("OFR0117")
            .SelectMany(d => d["data"]?["paths"]?.AsArray() ?? new JsonArray())
            .Select(p => p!.GetValue<string>())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var path in spelled)
        {
            var full = Path.GetFullPath(Path.Combine(repository, path));
            if (Path.Exists(full) || Resolve(full) is not { } actual)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (Directory.Exists(actual))
            {
                Directory.CreateSymbolicLink(full, actual);
            }
            else
            {
                File.CreateSymbolicLink(full, actual);
            }

            linked.Add(Path.GetRelativePath(repository, full).Replace('\\', '/'));
        }

        return linked;
    }

    /// <summary>
    /// The existing file or folder whose path equals <paramref name="path"/> ignoring case, or null. A folder may
    /// exist in several spellings (OLW has both <c>Video/YouTube</c> and <c>Video/Youtube</c>), so every spelling of
    /// each component is tried, the exact one first, until the whole path resolves.
    /// </summary>
    private static string? Resolve(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return Resolve(root, parts, 0);
    }

    private static string? Resolve(string current, string[] parts, int index)
    {
        if (index == parts.Length)
        {
            return current;
        }

        if (!Directory.Exists(current))
        {
            return null;
        }

        var exact = Path.Combine(current, parts[index]);
        var candidates = Directory.EnumerateFileSystemEntries(current)
            .Where(e => string.Equals(Path.GetFileName(e), parts[index], StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e == exact ? 0 : 1)
            .ThenBy(e => e, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (Resolve(candidate, parts, index + 1) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }
}
