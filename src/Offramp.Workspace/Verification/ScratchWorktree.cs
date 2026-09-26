using Offramp.Core.Caching;
using Offramp.Core.Git;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Verification;

/// <summary>
/// A throwaway copy of the repository for trying a change without touching the
/// user's working tree (consolidation restores proposed project files here). In a
/// git repository it is a detached <c>git worktree</c> of <c>HEAD</c> with the given
/// files copied over from the working tree, so uncommitted edits are included;
/// outside git it holds only those files. It lives under the system temporary
/// directory and is removed on dispose.
/// </summary>
public sealed class ScratchWorktree : IAsyncDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryRoot;
    private readonly bool _isWorktree;

    private ScratchWorktree(IGitService git, string repositoryRoot, string path, bool isWorktree)
    {
        _git = git;
        _repositoryRoot = repositoryRoot;
        _isWorktree = isWorktree;
        Path = path;
    }

    /// <summary>The scratch copy's root; repository-relative paths resolve against it.</summary>
    public string Path { get; }

    /// <param name="repositoryRoot">The repository to copy.</param>
    /// <param name="files">Repository-relative files copied from the working tree over the checkout (or into the empty directory outside git).</param>
    /// <param name="git">Git operations.</param>
    /// <param name="cancellationToken">Cancels the checkout.</param>
    public static async Task<ScratchWorktree> CreateAsync(string repositoryRoot, IEnumerable<string> files, IGitService git, CancellationToken cancellationToken = default)
    {
        var root = System.IO.Path.GetFullPath(repositoryRoot);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "offramp-scratch", ContentHash.Sha256(root)[..8] + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Only the top of a work tree (it holds .git) maps one to one onto a new work tree;
        // comparing paths instead would trip over symbolic links such as macOS's /var.
        var gitMarker = System.IO.Path.Combine(root, ".git");
        var isWorktree = (Directory.Exists(gitMarker) || File.Exists(gitMarker)) && await git.FindRepositoryRootAsync(root, cancellationToken) is not null;
        if (isWorktree)
        {
            await git.AddWorktreeAsync(root, path, cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        var scratch = new ScratchWorktree(git, root, path, isWorktree);
        foreach (var file in files)
        {
            scratch.CopyIn(file);
        }

        return scratch;
    }

    /// <summary>Copies a repository-relative file from the working tree into the scratch copy.</summary>
    public void CopyIn(string relativePath)
    {
        var source = RepoPaths.ToAbsolute(_repositoryRoot, relativePath);
        var target = Resolve(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    /// <summary>The absolute path of a repository-relative path inside the scratch copy.</summary>
    public string Resolve(string relativePath) => RepoPaths.ToAbsolute(Path, relativePath);

    public async ValueTask DisposeAsync()
    {
        if (_isWorktree)
        {
            try
            {
                await _git.RemoveWorktreeAsync(_repositoryRoot, Path);
                return;
            }
            catch (GitCommandException)
            {
                // Fall through: remove the directory; `git worktree prune` cleans the record later.
            }
        }

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
