using Offramp.Core.Processes;

namespace Offramp.Core.Git;

/// <summary>One line of <c>git status --porcelain=v1</c>.</summary>
/// <param name="Index">Staged status character (<c>R</c>, <c>M</c>, <c>A</c>, <c>?</c>, space).</param>
/// <param name="WorkTree">Unstaged status character.</param>
/// <param name="Path">Repository-relative path (the destination for renames).</param>
/// <param name="OriginalPath">The source path for renames and copies.</param>
public sealed record GitStatusEntry(char Index, char WorkTree, string Path, string? OriginalPath);

/// <summary>A file that differs from a commit: added, modified, or renamed (<paramref name="OriginalPath"/> is its path at the commit).</summary>
/// <param name="Path">Repository-relative path in the work tree.</param>
/// <param name="OriginalPath">The path at the commit for a rename; null for other changes.</param>
/// <param name="Status"><c>A</c> added (or untracked), <c>M</c> modified, <c>R</c> renamed, <c>C</c> copied, <c>T</c> type changed.</param>
public sealed record GitChange(string Path, string? OriginalPath, char Status);

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
    /// outside one, or for a file git does not track, it is <see cref="File.Move(string, string)"/>.
    /// Parent directories are created.
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

    /// <summary>The commit a revision names (<c>git rev-parse --verify REV^{commit}</c>), or null when it names none.</summary>
    Task<string?> ResolveCommitAsync(string repositoryRoot, string revision, CancellationToken cancellationToken = default);

    /// <summary>The best common ancestor of two revisions (<c>git merge-base</c>), or null when they have none.</summary>
    Task<string?> MergeBaseAsync(string repositoryRoot, string first, string second, CancellationToken cancellationToken = default);

    /// <summary>
    /// The files of the work tree that differ from a commit (<c>git diff --name-status -M COMMIT</c>, staged or not),
    /// and untracked files that are not ignored (status <c>A</c>). Deleted files are left out. Sorted by path.
    /// </summary>
    Task<IReadOnlyList<GitChange>> ChangesSinceAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default);
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

        // An untracked file (a class written a minute ago) has no history for git mv to keep: it moves as a plain file.
        var tracked = await runner.RunAsync(
            new ProcessSpec("git", ["ls-files", "--error-unmatch", "--", fromAbsolute]) { WorkingDirectory = root, Timeout = QuickTimeout },
            cancellationToken);
        if (!tracked.Succeeded)
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

    public async Task<string?> ResolveCommitAsync(string repositoryRoot, string revision, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["rev-parse", "--verify", "--quiet", revision + "^{commit}"]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        var commit = result.StandardOutput.Trim();
        return result.Succeeded && commit.Length > 0 ? commit : null;
    }

    public async Task<string?> MergeBaseAsync(string repositoryRoot, string first, string second, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            new ProcessSpec("git", ["merge-base", first, second]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        var commit = result.StandardOutput.Trim();
        return result.Succeeded && commit.Length > 0 ? commit : null;
    }

    public async Task<IReadOnlyList<GitChange>> ChangesSinceAsync(string repositoryRoot, string commit, CancellationToken cancellationToken = default)
    {
        var diff = await runner.RunAsync(
            new ProcessSpec("git", ["-c", "core.quotepath=off", "diff", "--name-status", "-M", "-z", commit, "--"]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        if (!diff.Succeeded)
        {
            throw new GitCommandException($"git diff failed: {diff.StandardError.Trim()}");
        }

        var untracked = await runner.RunAsync(
            new ProcessSpec("git", ["ls-files", "--others", "--exclude-standard", "-z"]) { WorkingDirectory = repositoryRoot, Timeout = QuickTimeout },
            cancellationToken);
        if (!untracked.Succeeded)
        {
            throw new GitCommandException($"git ls-files failed: {untracked.StandardError.Trim()}");
        }

        var changes = ParseNameStatusZ(diff.StandardOutput).ToDictionary(c => c.Path, StringComparer.Ordinal);
        foreach (var path in untracked.StandardOutput.Split('\0').Select(p => p.Trim('\r', '\n')).Where(p => p.Length > 0))
        {
            changes.TryAdd(path, new GitChange(path, null, 'A'));
        }

        return [.. changes.Values.OrderBy(c => c.Path, StringComparer.Ordinal)];
    }

    /// <summary>Parses <c>git diff --name-status -z</c> output; deletions are left out.</summary>
    public static IReadOnlyList<GitChange> ParseNameStatusZ(string output)
    {
        var changes = new List<GitChange>();
        var fields = output.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i++)
        {
            var status = fields[i].TrimStart('\n');
            if (status.Length == 0)
            {
                continue;
            }

            if (status[0] is 'R' or 'C' && i + 2 < fields.Length)
            {
                changes.Add(new GitChange(fields[i + 2], fields[i + 1], status[0]));
                i += 2;
            }
            else
            {
                if (status[0] != 'D')
                {
                    changes.Add(new GitChange(fields[i + 1], null, status[0]));
                }

                i++;
            }
        }

        return [.. changes.OrderBy(c => c.Path, StringComparer.Ordinal)];
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
