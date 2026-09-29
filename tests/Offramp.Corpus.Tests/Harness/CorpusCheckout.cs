using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Corpus.Tests.Harness;

/// <summary>
/// Gets a codebase at its pinned commit. The commit is fetched once into
/// <c>tests/.cache/corpus/&lt;name&gt;</c> (which CI caches by commit) and every run works on its own
/// fresh clone of that, so no run sees another's build output, restored packages, or edits.
/// </summary>
internal static class CorpusCheckout
{
    /// <summary>Written into the cache's <c>.git</c> folder last: a cache without it, or with another commit, is fetched again.</summary>
    private const string Marker = "offramp-corpus-commit";

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)];

    public static async Task<ScratchDirectory> FreshCopyAsync(CorpusCodebase codebase, CancellationToken cancellationToken)
    {
        var cache = RepositoryFiles.Path("tests", ".cache", "corpus", codebase.Name);
        var marker = Path.Combine(cache, ".git", Marker);
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != codebase.Commit)
        {
            await FetchAsync(codebase, cache, cancellationToken);
        }

        var copy = new ScratchDirectory("corpus-" + codebase.Name);
        Directory.Delete(copy.Path);
        await GitAsync(Path.GetDirectoryName(copy.Path)!, cancellationToken, "-c", "advice.detachedHead=false", "clone", "--quiet", cache, copy.Path);
        await AssertAtCommitAsync(copy.Path, codebase, cancellationToken);
        return copy;
    }

    /// <summary>
    /// Fetches only the pinned commit (GitHub serves any reachable commit by id) into a temporary folder
    /// that replaces the cache once complete, so an interrupted fetch never leaves a half cache behind.
    /// </summary>
    private static async Task FetchAsync(CorpusCodebase codebase, string cache, CancellationToken cancellationToken)
    {
        var partial = cache + ".partial";
        DeleteIfPresent(partial);
        Directory.CreateDirectory(partial);
        await GitAsync(partial, cancellationToken, "init", "--quiet");
        await GitAsync(partial, cancellationToken, "remote", "add", "origin", codebase.Repository);
        for (var attempt = 0; ; attempt++)
        {
            var fetch = await RunGitAsync(partial, cancellationToken, "fetch", "--quiet", "--depth", "1", "origin", codebase.Commit);
            if (fetch.Succeeded)
            {
                break;
            }

            Assert.True(attempt < RetryDelays.Length, $"Fetching {codebase.Commit} from {codebase.Repository} failed {attempt + 1} times: {fetch.StandardError}");
            await Task.Delay(RetryDelays[attempt], cancellationToken);
        }

        await GitAsync(partial, cancellationToken, "-c", "advice.detachedHead=false", "checkout", "--quiet", "FETCH_HEAD");
        await AssertAtCommitAsync(partial, codebase, cancellationToken);
        File.WriteAllText(Path.Combine(partial, ".git", Marker), codebase.Commit + "\n");
        DeleteIfPresent(cache);
        Directory.Move(partial, cache);
    }

    private static async Task AssertAtCommitAsync(string directory, CorpusCodebase codebase, CancellationToken cancellationToken)
    {
        var head = await RunGitAsync(directory, cancellationToken, "rev-parse", "HEAD");
        Assert.Equal(codebase.Commit, head.StandardOutput.Trim());
    }

    private static void DeleteIfPresent(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

    private static async Task GitAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var git = await RunGitAsync(directory, cancellationToken, arguments);
        Assert.True(git.Succeeded, $"git {string.Join(' ', arguments)} failed: {git.StandardError}");
    }

    private static Task<ProcessResult> RunGitAsync(string directory, CancellationToken cancellationToken, params string[] arguments) =>
        ProcessRunner.Instance.RunAsync(
            new ProcessSpec("git", arguments) { WorkingDirectory = directory, Timeout = TimeSpan.FromMinutes(30) },
            cancellationToken);
}
