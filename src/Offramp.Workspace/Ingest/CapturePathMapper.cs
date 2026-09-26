using Offramp.Core.Paths;

namespace Offramp.Workspace.Ingest;

/// <summary>
/// Maps absolute paths recorded on the machine that produced a log (possibly
/// Windows, possibly another checkout) onto repository-relative paths here.
/// The capture root is inferred from project paths: the longest suffix of a
/// captured project path that exists under the local repository root.
/// </summary>
public sealed class CapturePathMapper
{
    private readonly string _captureRoot;
    private readonly bool _ignoreCase;

    private CapturePathMapper(string repositoryRoot, string captureRoot)
    {
        RepositoryRoot = repositoryRoot;
        _captureRoot = Normalize(captureRoot).TrimEnd('/');
        _ignoreCase = IsWindowsStyle(captureRoot) || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    public string RepositoryRoot { get; }

    /// <summary>The inferred root on the capture machine, with forward slashes.</summary>
    public string CaptureRoot => _captureRoot;

    /// <summary>A mapper for logs produced in this repository on this machine.</summary>
    public static CapturePathMapper Local(string repositoryRoot) => new(repositoryRoot, repositoryRoot);

    /// <summary>
    /// Infers the capture root from the project paths in a log. Falls back to the
    /// local root when nothing matches (the log is then treated as local).
    /// </summary>
    public static CapturePathMapper Infer(string repositoryRoot, IEnumerable<string> capturedProjectPaths)
    {
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var captured in capturedProjectPaths)
        {
            var root = InferRoot(repositoryRoot, captured);
            if (root is not null)
            {
                votes[root] = votes.GetValueOrDefault(root) + 1;
            }
        }

        var best = votes
            .OrderByDescending(v => v.Value)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => v.Key)
            .FirstOrDefault();
        return new CapturePathMapper(repositoryRoot, best ?? repositoryRoot);
    }

    /// <summary>Repository-relative path, or null when the path is outside the captured repository.</summary>
    public string? ToRelative(string? capturedPath)
    {
        if (string.IsNullOrWhiteSpace(capturedPath))
        {
            return null;
        }

        var path = Normalize(capturedPath);
        var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.Equals(_captureRoot, comparison))
        {
            return "";
        }

        if (!path.StartsWith(_captureRoot + "/", comparison))
        {
            return null;
        }

        return Collapse(path[(_captureRoot.Length + 1)..]);
    }

    /// <summary>The local absolute path for a captured path, or null when outside the repository.</summary>
    public string? ToLocal(string? capturedPath)
    {
        var relative = ToRelative(capturedPath);
        return relative is null ? null : RepoPaths.ToAbsolute(RepositoryRoot, relative);
    }

    /// <summary>Resolves a path relative to a captured directory, then maps it.</summary>
    public string? ToRelative(string capturedDirectory, string pathMaybeRelative)
    {
        var normalized = Normalize(pathMaybeRelative);
        if (IsRooted(normalized))
        {
            return ToRelative(normalized);
        }

        return ToRelative(Normalize(capturedDirectory).TrimEnd('/') + "/" + normalized);
    }

    public static bool IsWindowsStyle(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsRooted(string normalized) =>
        normalized.StartsWith('/') || IsWindowsStyle(normalized);

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Resolves "." and ".." segments without touching the file system.</summary>
    private static string Collapse(string relative)
    {
        var parts = new List<string>();
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static string? InferRoot(string repositoryRoot, string capturedPath)
    {
        var segments = Normalize(capturedPath).Split('/');
        for (var start = 1; start < segments.Length; start++)
        {
            var suffix = string.Join('/', segments[start..]);
            if (suffix.Length == 0)
            {
                continue;
            }

            var local = Path.Combine(repositoryRoot, suffix.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(local))
            {
                return string.Join('/', segments[..start]);
            }
        }

        return null;
    }
}
