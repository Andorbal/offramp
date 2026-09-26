using Offramp.Core.Processes;
using Offramp.Workspace.Environment;

namespace Offramp.Fixtures;

/// <summary>Builds fake process runners describing a machine: which SDKs, git, and repository.</summary>
public sealed class FakeMachine
{
    public List<string> Sdks { get; } = ["8.0.404", "10.0.100"];

    /// <summary>What <c>dotnet --version</c> prints; null makes it fail like a global.json pin to a missing SDK.</summary>
    public string? SelectedSdk { get; set; } = "10.0.100";

    public bool DotnetInstalled { get; set; } = true;

    public string? GitVersion { get; set; } = "2.45.0";

    /// <summary>The git work tree top; null when not in a repository.</summary>
    public string? RepositoryRoot { get; set; }

    public ReferenceAssembliesResult ReferenceAssemblies { get; set; } = new(ReferenceAssembliesState.Cached, "1.0.3");

    /// <summary>Extra handlers applied to every runner this machine creates, after the built-in ones.</summary>
    public List<Action<FakeProcessRunner>> Setup { get; } = [];

    public FakeProcessRunner CreateRunner()
    {
        var runner = new FakeProcessRunner();
        if (DotnetInstalled)
        {
            runner.On("dotnet", ["--list-sdks"], 0, string.Concat(Sdks.Select(s => $"{s} [/usr/share/dotnet/sdk]\n")));
            runner.On("dotnet", ["--version"], _ => SelectedSdk is null
                ? new ProcessResult(145, "", "A compatible .NET SDK was not found.\n\nRequested SDK version: 10.0.999\n")
                : new ProcessResult(0, SelectedSdk + "\n", ""));
        }

        if (GitVersion is not null)
        {
            runner.On("git", ["--version"], 0, $"git version {GitVersion}\n");
            runner.On("git", ["rev-parse", "--show-toplevel"], _ => RepositoryRoot is null
                ? new ProcessResult(128, "", "fatal: not a git repository (or any of the parent directories): .git\n")
                : new ProcessResult(0, RepositoryRoot.Replace('\\', '/') + "\n", ""));
        }

        foreach (var setup in Setup)
        {
            setup(runner);
        }

        return runner;
    }

    public FakeReferenceAssembliesProbe CreateReferenceAssembliesProbe() => new(ReferenceAssemblies);
}

public sealed class FakeReferenceAssembliesProbe(ReferenceAssembliesResult result) : IReferenceAssembliesProbe
{
    public Task<ReferenceAssembliesResult> ProbeAsync(string repositoryRoot, CancellationToken cancellationToken) =>
        Task.FromResult(result);
}
