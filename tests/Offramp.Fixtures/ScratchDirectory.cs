namespace Offramp.Fixtures;

/// <summary>A unique temporary directory, deleted (including read-only git objects) on dispose.</summary>
public sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory(string? prefix = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "offramp-tests", (prefix ?? "t") + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);

        // Resolve symlinks (macOS /var → /private/var) so paths match what git reports.
        Path = new DirectoryInfo(Path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? RealPath(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file (creating directories) with LF line endings and no BOM.</summary>
    public string Write(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }

    public string Read(string relativePath) => File.ReadAllText(Combine(relativePath.Split('/')));

    public bool Exists(string relativePath) => File.Exists(Combine(relativePath.Split('/')));

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS cleans temp eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RealPath(string path)
    {
        // On macOS the temp folder lives behind the /var → /private/var symlink.
        if (OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal))
        {
            return "/private" + path;
        }

        // On Windows runners TEMP can contain 8.3 short names (RUNNER~1) that git expands.
        return OperatingSystem.IsWindows() ? LongPath(path) : path;
    }

    private static string LongPath(string path)
    {
        var root = System.IO.Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Directory.EnumerateFileSystemEntries(current, segment).FirstOrDefault();
            current = match ?? System.IO.Path.Combine(current, segment);
        }

        return current;
    }
}
