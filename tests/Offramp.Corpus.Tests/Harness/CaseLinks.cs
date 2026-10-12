using System.Text.Json.Nodes;

namespace Offramp.Corpus.Tests.Harness;

/// <summary>
/// The fix <c>OFR0117</c> prescribes for a path spelled in another letter case than the file on disk, applied as a
/// user on Linux would apply it without editing the codebase: a symbolic link under the spelling the project uses.
/// Each link is made at the first segment in the wrong case, as a case-insensitive file system resolves the path, so a
/// project in a folder spelled the wrong way (MassTransit's solution lists <c>MassTransit.AutofacIntegration</c>, the
/// folder is <c>AutoFacIntegration</c>) still finds the files beside it. Windows and macOS file systems ignore case,
/// so there is nothing to do there.
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
            if (Path.Exists(full) || Resolve(full) is null)
            {
                continue;
            }

            Link(full);
            linked.Add(Path.GetRelativePath(repository, full).Replace('\\', '/'));
        }

        return linked;
    }

    /// <summary>
    /// Links each segment of <paramref name="path"/> that exists only in another spelling, from the root down, to the
    /// spelling through which the rest of the path resolves. Where a segment exists as spelled but the rest resolves
    /// only through another spelling of it (OLW's two <c>Video</c> folders), the whole path is linked instead.
    /// </summary>
    private static void Link(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            if (!Path.Exists(next))
            {
                if (Candidates(current, parts[i]).FirstOrDefault(c => Resolve(c, parts, i + 1) is not null) is not { } actual)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    Create(path, Resolve(path)!);
                    return;
                }

                Create(next, actual);
            }

            current = next;
        }
    }

    private static void Create(string link, string target)
    {
        if (Directory.Exists(target))
        {
            Directory.CreateSymbolicLink(link, target);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
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

        foreach (var candidate in Candidates(current, parts[index]))
        {
            if (Resolve(candidate, parts, index + 1) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>The entries of <paramref name="folder"/> named <paramref name="name"/> ignoring case, the exact one first.</summary>
    private static IEnumerable<string> Candidates(string folder, string name)
    {
        var exact = Path.Combine(folder, name);
        return Directory.EnumerateFileSystemEntries(folder)
            .Where(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e == exact ? 0 : 1)
            .ThenBy(e => e, StringComparer.Ordinal);
    }
}
