using Offramp.Core.Git;

namespace Offramp.Core.Paths;

/// <summary>How the repository root was determined.</summary>
public enum RepositoryRootSource
{
    Git,
    ConfigFile,
    WorkingDirectory,
}

public sealed record RepositoryRoot(string Path, RepositoryRootSource Source);

/// <summary>
/// Finds the repository root: the git work tree top, else the nearest ancestor
/// holding <c>offramp.yml</c>, else the working directory
/// (<c>docs/decisions/0002-cli-foundations.md</c>).
/// </summary>
public static class RepositoryLocator
{
    public static async Task<RepositoryRoot> LocateAsync(string workingDirectory, IGitService git, CancellationToken cancellationToken = default)
    {
        var fromGit = await git.FindRepositoryRootAsync(workingDirectory, cancellationToken);
        if (fromGit is not null)
        {
            return new RepositoryRoot(fromGit, RepositoryRootSource.Git);
        }

        for (var dir = new DirectoryInfo(workingDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "offramp.yml")))
            {
                return new RepositoryRoot(dir.FullName, RepositoryRootSource.ConfigFile);
            }
        }

        return new RepositoryRoot(System.IO.Path.GetFullPath(workingDirectory), RepositoryRootSource.WorkingDirectory);
    }
}
