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

    /// <summary>The existing file or folder whose path equals <paramref name="path"/> ignoring case, or null.</summary>
    private static string? Resolve(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = Path.Combine(current, part);
            if (Path.Exists(exact))
            {
                current = exact;
                continue;
            }

            var match = Directory.Exists(current)
                ? Directory.EnumerateFileSystemEntries(current)
                    .Where(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }
}
