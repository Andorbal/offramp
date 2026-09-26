using Offramp.Core.Processes;

namespace Offramp.Core.Git;

/// <summary>One line of <c>git status --porcelain=v1</c>.</summary>
/// <param name="Index">Staged status character (<c>R</c>, <c>M</c>, <c>A</c>, <c>?</c>, space).</param>
/// <param name="WorkTree">Unstaged status character.</param>
/// <param name="Path">Repository-relative path (the destination for renames).</param>
/// <param name="OriginalPath">The source path for renames and copies.</param>
public sealed record GitStatusEntry(char Index, char WorkTree, string Path, string? OriginalPath);

/// <summary>
/// Git operations Offramp needs: detect the repository, stage renames with
/// <c>git mv</c>, and read status. Offramp never commits.
/// </summary>
public interface IGitService
{
    /// <summary>The installed git version, or null when git cannot be started.</summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>The top of the work tree containing <paramref name="directory"/>, or null outside a repository.</summary>
    Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a file. Inside a repository this is <c>git mv</c> (a staged rename);
    /// outside one it is <see cref="File.Move(string, string)"/>. Parent directories are created.
    /// </summary>
    Task MoveAsync(string repositoryRoot, string fromAbsolute, string toAbsolute, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string repositoryRoot, CancellationToken cancellationToken = default);

    /// <summary>Checks out <c>HEAD</c> into a new detached work tree at <paramref name="path"/> (<c>git worktree add --detach</c>).</summary>
    Task AddWorktreeAsync(string repositoryRoot, string path, CancellationToken cancellationToken = default) =>
        AddWorktreeAsync(repositoryRoot, path, "HEAD", cancellationToken);

    /// <summary>Adds a detached work tree at <paramref name="path"/> checked out at <paramref name="revision"/>.</summary>
    Task AddWorktreeAsync(string repositoryRoot, string path, string revision, CancellationToken cancellationToken = default);

    /// <summary>Removes a work tree added by <c>AddWorktreeAsync</c>, discarding its changes.</summary>
    Task RemoveWorktreeAsync(string repositoryRoot, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The files under a repository-relative folder at a revision (<c>git ls-tree -r --name-only</c>),
    /// repository-relative and sorted; null when the revision does not exist.
    /// </summary>
    Task<IReadOnlyList<string>?> ListFilesAsync(string repositoryRoot, string revision, string folder, CancellationToken cancellationToken = default);

    /// <summary>A file's text at a revision (<c>git show REV:PATH</c>), or null when it is not there.</summary>
    Task<string?> ShowFileAsync(string repositoryRoot, string revision, string path, CancellationToken cancellationToken = default);
}

/// <summary>Git through the command line.</summary>
public sealed class GitService(IProcessRunner runner) : IGitService
{
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(new ProcessSpec("git", ["--version"]) { Timeout = QuickTimeout }, cancellationToken);
        if (!result.Succeeded)
        {
            return null;
        }

        // "git version 2.43.0" or "git version 2.39.3 (Apple Git-145)"
        var parts = result.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : result.StandardOutput.Trim();
    }

    public async Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["rev-parse", "--show-toplevel"]) { WorkingDirectory = directory, Timeout = QuickTimeout },
            cancellationToken);
        if (!result.Succeeded)
        {
            return null;
        }

        var top = result.StandardOutput.Trim();
        return top.Length == 0 ? null : Path.GetFullPath(top);
    }

    public async Task MoveAsync(string repositoryRoot, string fromAbsolute, string toAbsolute, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(toAbsolute);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var root = await FindRepositoryRootAsync(repositoryRoot, cancellationToken);
        if (root is null)
        {
            File.Move(fromAbsolute, toAbsolute);
            return;
        }

        var result = await runner.RunAsync(
            new ProcessSpec("git", ["mv", "--", fromAbsolute, toAbsolute]) { WorkingDirectory = root, Timeout = QuickTimeout },
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitCommandException($"git mv failed: {result.StandardError.Trim()}");
        }
    }

    public async Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["status", "--porcelain=v1", "-z", "--untracked-files=all"])
            {
                WorkingDirectory = repositoryRoot,
                Timeout = QuickTimeout,
            },
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitCommandException($"git status failed: {result.StandardError.Trim()}");
        }

        return ParsePorcelainZ(result.StandardOutput);
    }

    public async Task AddWorktreeAsync(string repositoryRoot, string path, string revision, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["worktree", "add", "--detach", "--quiet", path, revision]) { WorkingDirectory = repositoryRoot, Timeout = TimeSpan.FromMinutes(10) },
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitCommandException($"git worktree add failed: {result.StandardError.Trim()}");
        }
    }

    public async Task RemoveWorktreeAsync(string repositoryRoot, string path, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["worktree", "remove", "--force", path]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitCommandException($"git worktree remove failed: {result.StandardError.Trim()}");
        }
    }

    public async Task<IReadOnlyList<string>?> ListFilesAsync(string repositoryRoot, string revision, string folder, CancellationToken cancellationToken = default)
    {
        var verify = await runner.RunAsync(
            new ProcessSpec("git", ["rev-parse", "--verify", "--quiet", revision + "^{commit}"]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        if (!verify.Succeeded)
        {
            return null;
        }

        string[] arguments = folder.Length == 0 ? ["ls-tree", "-r", "-z", "--name-only", revision] : ["ls-tree", "-r", "-z", "--name-only", revision, "--", folder + "/"];
        var result = await runner.RunAsync(new ProcessSpec("git", arguments) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout }, cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitCommandException($"git ls-tree failed: {result.StandardError.Trim()}");
        }

        return [.. result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];
    }

    public async Task<string?> ShowFileAsync(string repositoryRoot, string revision, string path, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["show", revision + ":" + path]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        return result.Succeeded ? result.StandardOutput : null;
    }

    /// <summary>Parses <c>git status --porcelain=v1 -z</c> output.</summary>
    public static IReadOnlyList<GitStatusEntry> ParsePorcelainZ(string output)
    {
        var entries = new List<GitStatusEntry>();
        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i].TrimStart('\n');
            if (field.Length < 4)
            {
                continue;
            }

            var index = field[0];
            var workTree = field[1];
            var path = field[3..];
            string? original = null;
            if (index is 'R' or 'C' && i + 1 < fields.Length)
            {
                original = fields[++i];
            }

            entries.Add(new GitStatusEntry(index, workTree, path, original));
        }

        return [.. entries.OrderBy(e => e.Path, StringComparer.Ordinal)];
    }
}

/// <summary>A git command Offramp depends on failed.</summary>
public sealed class GitCommandException(string message) : Exception(message);
