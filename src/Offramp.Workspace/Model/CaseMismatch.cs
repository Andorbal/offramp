namespace Offramp.Workspace.Model;

/// <summary>
/// A path that is not on disk as spelled but is in another letter case: it resolves on Windows and
/// macOS, whose file systems ignore case by default, and not on Linux (OFR0117).
/// </summary>
/// <param name="Spelled">The path as the build asked for it.</param>
/// <param name="OnDisk">The path as it is on disk.</param>
/// <param name="Evidence">The path as spelled and its first segment that differs; <c>scan</c> makes the path repository-relative.</param>
internal sealed record CaseMismatch(string Spelled, string OnDisk, string Evidence)
{
    /// <summary>The mismatch for an absolute <paramref name="path"/>, or null when it exists as spelled or not at all.</summary>
    public static CaseMismatch? Find(string path)
    {
        var spelled = path.Replace('\\', '/');
        var root = Path.GetPathRoot(spelled);
        if (string.IsNullOrEmpty(root) || File.Exists(spelled) || Directory.Exists(spelled))
        {
            return null;
        }

        var segments = spelled[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var first = -1;
        var actual = "";
        for (var i = 0; i < segments.Length; i++)
        {
            var exact = Path.Combine(current, segments[i]);
            if (File.Exists(exact) || Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            var match = Directory.Exists(current)
                ? Directory.EnumerateFileSystemEntries(current)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(name => string.Equals(name, segments[i], StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault()
                : null;
            if (match is null)
            {
                return null;
            }

            if (first < 0)
            {
                first = i;
                actual = match;
            }

            current = Path.Combine(current, match);
        }

        return first < 0
            ? null
            : new CaseMismatch(spelled, current, $"{spelled}: '{segments[first]}' is '{actual}' on disk");
    }
}
