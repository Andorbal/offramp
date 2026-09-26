using Offramp.Core.Processes;

namespace Offramp.Fixtures;

/// <summary>
/// A copy of a fixture under <c>tests/fixtures/</c> in a scratch directory,
/// initialized as a git repository with one commit, so commands see a real,
/// standalone repository (docs/spec/04-testing-and-fixtures.md).
/// </summary>
public sealed class FixtureRepository : IDisposable
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".offramp", ".git" };

    private FixtureRepository(string name, ScratchDirectory directory)
    {
        Name = name;
        Directory = directory;
    }

    public string Name { get; }

    public ScratchDirectory Directory { get; }

    public string Path => Directory.Path;

    public static string SourcePath(string name) => RepositoryFiles.Path("tests", "fixtures", name);

    public static Task<FixtureRepository> CreateAsync(string name, bool git = true) =>
        CreateAsync(name, path => CopyDirectory(SourcePath(name), path), git);

    /// <summary>A repository whose files <paramref name="generate"/> writes (see <see cref="GeneratedFixtures"/>).</summary>
    public static async Task<FixtureRepository> CreateAsync(string name, Action<string> generate, bool git = true)
    {
        var directory = new ScratchDirectory(name);
        generate(directory.Path);
        var repository = new FixtureRepository(name, directory);
        if (git)
        {
            await repository.GitAsync("init", "-q");
            await repository.GitAsync("config", "user.email", "fixtures@offramp.test");
            await repository.GitAsync("config", "user.name", "Offramp Fixtures");
            await repository.GitAsync("config", "core.autocrlf", "false");
            await repository.GitAsync("add", "-A");
            await repository.GitAsync("commit", "-q", "-m", "fixture");
        }

        return repository;
    }

    public async Task<ProcessResult> GitAsync(params string[] arguments)
    {
        var result = await ProcessRunner.Instance.RunAsync(new ProcessSpec("git", arguments) { WorkingDirectory = Path });
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        }

        return result;
    }

    public void Dispose() => Directory.Dispose();

    public static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), overwrite: true);
        }

        foreach (var sub in System.IO.Directory.EnumerateDirectories(source))
        {
            var name = System.IO.Path.GetFileName(sub);
            if (!SkippedDirectories.Contains(name))
            {
                CopyDirectory(sub, System.IO.Path.Combine(destination, name));
            }
        }
    }
}
