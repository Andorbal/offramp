using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Paths;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Environment;

namespace Offramp.Workspace.Tests;

public sealed class DoctorRunnerTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("doctor");

    public void Dispose() => _repo.Dispose();

    private async Task<(DoctorReport Report, DiagnosticBag Diagnostics)> RunAsync(FakeMachine machine, int target = 10)
    {
        var runner = machine.CreateRunner();
        var git = new GitService(runner);
        var repository = await RepositoryLocator.LocateAsync(_repo.Path, git);
        var config = ConfigLoader.Load(new ConfigSources
        {
            RepositoryRoot = repository.Path,
            CommandLine = new System.Text.Json.Nodes.JsonObject { ["target"] = target },
        });
        var bag = new DiagnosticBag();
        bag.AddRange(config.Diagnostics);
        var report = await DoctorRunner.RunAsync(new DoctorContext
        {
            Repository = repository,
            Config = config,
            WorkspacePath = Path.Combine(repository.Path, ".offramp", "workspace.json"),
            Processes = runner,
            Git = git,
            ReferenceAssemblies = machine.CreateReferenceAssembliesProbe(),
            Diagnostics = bag,
            Os = "linux-x64",
        }, CancellationToken.None);
        return (report, bag);
    }

    private FakeMachine Healthy() => new() { RepositoryRoot = _repo.Path };

    private static CheckStatus Status(DoctorReport report, string id) => report.Checks.Single(c => c.Id == id).Status;

    [Fact]
    public async Task A_healthy_machine_passes_every_environment_check()
    {
        _repo.Write("offramp.yml", "target: 10\n");
        _repo.Write(".offramp/workspace.json", "{}");

        var (report, bag) = await RunAsync(Healthy());

        Assert.All(report.Checks, c => Assert.Equal(CheckStatus.Pass, c.Status));
        Assert.Equal(0, bag.Count);
        Assert.Equal(["8.0.404", "10.0.100"], report.Environment.Sdks);
        Assert.Equal("10.0.100", report.Environment.SelectedSdk);
        Assert.True(report.Environment.Git.Repository);
    }

    [Fact]
    public async Task Report_matches_the_snapshot()
    {
        var (report, _) = await RunAsync(Healthy());
        var json = Offramp.Core.Json.OfframpJson.Serialize(report, WorkspaceJsonContext.Default.DoctorReport);
        SchemaAssert.Valid("doctor", json);
        await Verify(Scrub.Text(json, _repo.Path), extension: "json");
    }

    /// <summary>Roadmap M0: doctor on a machine without git (mocked).</summary>
    [Fact]
    [ProducesDiagnostic("OFR0014")]
    public async Task Without_git_the_git_check_warns_and_the_repository_check_is_skipped()
    {
        var (report, bag) = await RunAsync(new FakeMachine { GitVersion = null, RepositoryRoot = _repo.Path });

        Assert.Equal(CheckStatus.Warn, Status(report, "git"));
        Assert.Equal(CheckStatus.Skip, Status(report, "git-repository"));
        Assert.Null(report.Environment.Git.Version);
        Assert.False(report.Environment.Git.Repository);
        Assert.Equal(Severity.Warning, bag.ToSortedList().Single(d => d.Code == "OFR0014").Severity);
        Assert.Equal(0, report.Summary.Fail);
    }

    [Fact]
    [ProducesDiagnostic("OFR0015")]
    public async Task Outside_a_repository_the_repository_check_warns()
    {
        var (report, bag) = await RunAsync(new FakeMachine { RepositoryRoot = null });

        Assert.Equal(CheckStatus.Warn, Status(report, "git-repository"));
        Assert.True(bag.Contains("OFR0015"));
    }

    [Fact]
    [ProducesDiagnostic("OFR0010")]
    public async Task Without_dotnet_the_sdk_check_fails_and_dependent_checks_are_skipped()
    {
        var (report, bag) = await RunAsync(new FakeMachine { DotnetInstalled = false, RepositoryRoot = _repo.Path });

        Assert.Equal(CheckStatus.Fail, Status(report, "dotnet-sdk"));
        Assert.Equal(CheckStatus.Skip, Status(report, "global-json"));
        Assert.Equal(CheckStatus.Skip, Status(report, "target"));
        Assert.Equal(Severity.Error, bag.ToSortedList().Single(d => d.Code == "OFR0010").Severity);
        Assert.Contains(report.Checks, c => c.Id == "dotnet-sdk" && c.Remedy!.Contains("https://dot.net", StringComparison.Ordinal));
    }

    [Fact]
    [ProducesDiagnostic("OFR0011")]
    public async Task A_global_json_pin_to_a_missing_sdk_fails_selection()
    {
        _repo.Write("global.json", "{ \"sdk\": { \"version\": \"10.0.999\", \"rollForward\": \"disable\" } }");

        var (report, bag) = await RunAsync(new FakeMachine { SelectedSdk = null, RepositoryRoot = _repo.Path });

        var check = report.Checks.Single(c => c.Id == "global-json");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("10.0.999", check.Message, StringComparison.Ordinal);
        Assert.Contains("rollForward", check.Remedy!, StringComparison.Ordinal);
        Assert.Equal(new GlobalJsonInfo("global.json", "10.0.999", "disable"), report.Environment.GlobalJson);
        Assert.True(bag.Contains("OFR0011"));
    }

    [Fact]
    [ProducesDiagnostic("OFR0012")]
    public async Task An_sdk_older_than_the_target_fails_the_target_check()
    {
        var machine = Healthy();
        machine.SelectedSdk = "8.0.404";

        var (report, bag) = await RunAsync(machine, target: 10);

        var check = report.Checks.Single(c => c.Id == "target");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("--target 8", check.Remedy!, StringComparison.Ordinal);
        Assert.True(bag.Contains("OFR0012"));

        var (older, _) = await RunAsync(machine, target: 8);
        Assert.Equal(CheckStatus.Pass, Status(older, "target"));
    }

    [Fact]
    [ProducesDiagnostic("OFR0013")]
    public async Task Missing_reference_assemblies_fail()
    {
        var machine = Healthy();
        machine.ReferenceAssemblies = new ReferenceAssembliesResult(ReferenceAssembliesState.NotFound, null);

        var (report, bag) = await RunAsync(machine);

        Assert.Equal(CheckStatus.Fail, Status(report, "reference-assemblies"));
        Assert.True(bag.Contains("OFR0013"));
    }

    [Fact]
    [ProducesDiagnostic("OFR1006")]
    public async Task Unreachable_feeds_warn()
    {
        var machine = Healthy();
        machine.ReferenceAssemblies = new ReferenceAssembliesResult(ReferenceAssembliesState.FeedUnreachable, "corp-feed");

        var (report, bag) = await RunAsync(machine);

        Assert.Equal(CheckStatus.Warn, Status(report, "reference-assemblies"));
        Assert.Equal(Severity.Warning, bag.ToSortedList().Single(d => d.Code == "OFR1006").Severity);
    }

    [Theory]
    [InlineData(ReferenceAssembliesState.Cached, "1.0.3")]
    [InlineData(ReferenceAssembliesState.TargetingPack, "v4.8")]
    [InlineData(ReferenceAssembliesState.AvailableFromFeed, "nuget.org")]
    public async Task Resolvable_reference_assemblies_pass(ReferenceAssembliesState state, string detail)
    {
        var machine = Healthy();
        machine.ReferenceAssemblies = new ReferenceAssembliesResult(state, detail);

        var (report, _) = await RunAsync(machine);

        Assert.Equal(CheckStatus.Pass, Status(report, "reference-assemblies"));
    }

    [Fact]
    [ProducesDiagnostic("OFR0001")]
    public async Task A_missing_workspace_model_is_a_warning_in_doctor()
    {
        var (report, bag) = await RunAsync(Healthy());

        Assert.Equal(CheckStatus.Warn, Status(report, "workspace"));
        var diagnostic = bag.ToSortedList().Single(d => d.Code == "OFR0001");
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        Assert.Equal(".offramp/workspace.json", diagnostic.Data["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configuration_problems_are_reported_by_the_config_check()
    {
        _repo.Write("offramp.yml", "target: 10\nverify:\n  mode: compile\n");
        var (failing, _) = await RunAsync(Healthy());
        Assert.Equal(CheckStatus.Fail, Status(failing, "config"));
        Assert.Equal(["OFR0053"], failing.Checks.Single(c => c.Id == "config").Codes);

        _repo.Write("offramp.yml", "target: 10\nverfy: {}\n");
        var (warning, _) = await RunAsync(Healthy());
        Assert.Equal(CheckStatus.Warn, Status(warning, "config"));
    }

    [Fact]
    public async Task Checks_always_appear_in_the_same_order()
    {
        var (report, _) = await RunAsync(new FakeMachine { DotnetInstalled = false, GitVersion = null });

        Assert.Equal(
            ["dotnet-sdk", "global-json", "target", "reference-assemblies", "git", "git-repository", "config", "workspace"],
            report.Checks.Select(c => c.Id));
        Assert.Equal(report.Checks.Count, report.Summary.Pass + report.Summary.Warn + report.Summary.Fail + report.Summary.Skip);
    }
}
