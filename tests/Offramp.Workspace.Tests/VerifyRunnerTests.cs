using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Store;
using Offramp.Workspace.Verification;
using VerifyResult = Offramp.Workspace.Verification.VerifyResult;

namespace Offramp.Workspace.Tests;

/// <summary>docs/spec/commands/workspace.md#verify; ROADMAP M3 acceptance.</summary>
public sealed class VerifyRunnerTests
{
    private const string Broken = "namespace Shared { public class Broken { public int X => Missing.Value; } }\n";

    [Fact]
    [ProducesDiagnostic("OFR5001")]
    public async Task An_injected_error_fails_verification_and_removing_it_passes()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        var model = WorkspaceStore.Read(fixture.WorkspacePath);

        var clean = await RunAsync(fixture.Root, model, new DiagnosticBag());
        Assert.Equal(VerifyStatus.Passed, clean.Status);
        Assert.Equal("DualTarget.slnx", Assert.Single(clean.Invocations).CommandLine.Split(' ')[2]);

        repository.Directory.Write("src/Shared/Broken.cs", Broken);
        var diagnostics = new DiagnosticBag();
        var broken = await RunAsync(fixture.Root, model, diagnostics);

        Assert.Equal(VerifyStatus.Failed, broken.Status);
        Assert.False(broken.Passed);
        var group = Assert.Single(broken.Errors);
        Assert.Equal(("CS0103", 1, "src/Shared/Broken.cs", "src/Shared/Shared.csproj"), (group.Code, group.Count, group.First.File, group.First.Project));
        Assert.Equal(VerifyProjectStatus.Failed, broken.Projects.Single(p => p.Project == "src/Shared/Shared.csproj").Status);
        Assert.Equal(VerifyProjectStatus.NotVerified, broken.Projects.Single(p => p.Project == "src/Tool/Tool.csproj").Status);
        Assert.True(diagnostics.Contains("OFR5001"));
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".offramp", "verify", "verify.binlog")));

        File.Delete(Path.Combine(fixture.Root, "src", "Shared", "Broken.cs"));
        Assert.Equal(VerifyStatus.Passed, (await RunAsync(fixture.Root, model, new DiagnosticBag())).Status);
    }

    [Fact]
    [ProducesDiagnostic("OFR5010")]
    public async Task A_baseline_excuses_known_errors_and_flags_new_codes()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        repository.Directory.Write("src/Shared/Broken.cs", Broken);

        var recording = await RunAsync(fixture.Root, model, new DiagnosticBag(), recordBaseline: true);
        Assert.Equal(VerifyStatus.Passed, recording.Status);
        Assert.Equal(VerifyRunner.BaselineFile, recording.BaselineRecorded);
        Assert.Equal(1, recording.Baseline!.Known);

        // A later edit elsewhere moves the known error down a line; it is still known.
        repository.Directory.Write("src/Shared/Broken.cs", "// moved\n" + Broken);
        var known = await RunAsync(fixture.Root, model, new DiagnosticBag());
        Assert.Equal(VerifyStatus.Passed, known.Status);
        Assert.Empty(known.Errors);

        repository.Directory.Write("src/Shared/Worse.cs", "namespace Shared { public class Worse { public string Y => 1; } }\n");
        var diagnostics = new DiagnosticBag();
        var worse = await RunAsync(fixture.Root, model, diagnostics);
        Assert.Equal(VerifyStatus.Failed, worse.Status);
        Assert.Equal("CS0029", Assert.Single(worse.Errors).Code);
        Assert.Equal(["CS0029"], worse.Baseline!.NewCodes);
        Assert.True(diagnostics.Contains("OFR5010"));
    }

    [Fact]
    public async Task A_selection_builds_a_solution_filter_of_it()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        var runner = new FakeProcessRunner().On("dotnet", ["build"], 0, "");

        var result = await VerifyRunner.RunAsync(Request(fixture.Root, model, new DiagnosticBag(), runner) with
        {
            Projects = ["src/Contracts/Contracts.csproj"],
            Everything = false,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(VerifyStatus.Passed, result!.Status);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(".offramp/verify/verify.slnf", call.Arguments[1]);
        var filter = File.ReadAllText(Path.Combine(fixture.Root, ".offramp", "verify", "verify.slnf"));
        Assert.Contains(@"..\\..\\DualTarget.slnx", filter, StringComparison.Ordinal);
        Assert.Contains(@"src\\Contracts\\Contracts.csproj", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_arguments_carry_the_configuration()
    {
        var config = new VerifyConfig
        {
            Configuration = "Release",
            Restore = false,
            Properties = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["TreatWarningsAsErrors"] = "false", ["A"] = "1" },
            WarnAsError = ["CS0168", "CS0219"],
            NoWarn = ["CS1591", "NU1603"],
        };

        Assert.Equal(
            ["build", "App.sln", "-nologo", "-v:minimal", "-nodeReuse:false", "-bl:.offramp/verify/verify.binlog", "-c", "Release", "--no-restore",
             "-p:A=1", "-p:TreatWarningsAsErrors=false", "-warnaserror:CS0168;CS0219", "-nowarn:CS1591;NU1603"],
            VerifyRunner.BuildArguments("App.sln", ".offramp/verify/verify.binlog", config));
    }

    [Fact]
    [ProducesDiagnostic("OFR5002")]
    public async Task A_timeout_is_reported_and_nothing_is_verified()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        var runner = new FakeProcessRunner().On("dotnet", ["build"], _ => new ProcessResult(-1, "", "") { TimedOut = true });
        var diagnostics = new DiagnosticBag();

        var result = await VerifyRunner.RunAsync(Request(fixture.Root, model, diagnostics, runner), TestContext.Current.CancellationToken);

        Assert.Equal(VerifyStatus.TimedOut, result!.Status);
        Assert.True(diagnostics.Contains("OFR5002"));
        Assert.All(result.Projects, p => Assert.Equal(VerifyProjectStatus.NotVerified, p.Status));
    }

    [Fact]
    public async Task A_missing_sdk_is_an_environment_failure()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var diagnostics = new DiagnosticBag();

        var result = await VerifyRunner.RunAsync(
            Request(fixture.Root, WorkspaceStore.Read(fixture.WorkspacePath), diagnostics, new FakeProcessRunner()), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(diagnostics.Contains("OFR0010"));
    }

    [Fact]
    [ProducesDiagnostic("OFR5090")]
    public async Task Mode_none_skips_and_says_so()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var runner = new FakeProcessRunner();
        var diagnostics = new DiagnosticBag();

        var result = await VerifyRunner.RunAsync(
            Request(fixture.Root, WorkspaceStore.Read(fixture.WorkspacePath), diagnostics, runner) with { Mode = VerifyMode.None }, TestContext.Current.CancellationToken);

        Assert.Equal(VerifyStatus.Skipped, result!.Status);
        Assert.True(result.Passed);
        Assert.Empty(runner.Calls);
        Assert.True(diagnostics.Contains("OFR5090"));
    }

    [Fact]
    [ProducesDiagnostic("OFR5020")]
    public async Task Command_mode_merges_the_envelope_and_passes_the_environment()
    {
        using var directory = new ScratchDirectory("verify-command");
        directory.Write("envelope.json", """
            { "diagnostics": [
              { "code": "LINT042", "severity": "error", "message": "Banned API", "file": "src/Core/A.cs", "line": 3 },
              { "code": "LINT007", "severity": "warning", "message": "Consider this" } ] }
            """);
        var command = OperatingSystem.IsWindows()
            ? "echo %OFFRAMP_VERIFY_PROJECTS%^|%OFFRAMP_VERIFY_TARGET%> seen.txt & type envelope.json & exit /b 3"
            : "printf '%s|%s' \"$OFFRAMP_VERIFY_PROJECTS\" \"$OFFRAMP_VERIFY_TARGET\" > seen.txt; cat envelope.json; exit 3";
        var model = FixtureModels.Load("dual-target");
        var diagnostics = new DiagnosticBag();

        var result = await VerifyRunner.RunAsync(
            Request(directory.Path, model, diagnostics, ProcessRunner.Instance) with
            {
                Mode = VerifyMode.Command,
                Config = new VerifyConfig { Mode = "command", Command = command },
                Projects = ["src/Contracts/Contracts.csproj", "src/Shared/Shared.csproj"],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(VerifyStatus.Failed, result!.Status);
        Assert.Equal(3, Assert.Single(result.Invocations).ExitCode);
        Assert.Equal(("LINT042", "src/Core/A.cs"), (Assert.Single(result.Errors).Code, result.Errors[0].First.File));
        var merged = diagnostics.ToSortedList().Where(d => d.Code == "OFR5020").ToList();
        Assert.Equal(["LINT007: Consider this", "LINT042: Banned API"], merged.Select(d => d.Message).Order(StringComparer.Ordinal));
        Assert.Equal(Severity.Error, merged.Single(d => d.Message.StartsWith("LINT042", StringComparison.Ordinal)).Severity);
        Assert.Equal("src/Contracts/Contracts.csproj;src/Shared/Shared.csproj|net10.0", File.ReadAllText(directory.Combine("seen.txt")).Trim());
    }

    [Fact]
    public async Task A_failing_command_without_an_envelope_keeps_its_output()
    {
        using var directory = new ScratchDirectory("verify-command");
        var command = OperatingSystem.IsWindows() ? "echo tests failed & exit /b 1" : "echo tests failed; exit 1";

        var result = await VerifyRunner.RunAsync(
            Request(directory.Path, FixtureModels.Load("dual-target"), new DiagnosticBag(), ProcessRunner.Instance) with
            {
                Mode = VerifyMode.Command,
                Config = new VerifyConfig { Mode = "command", Command = command },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(VerifyStatus.Failed, result!.Status);
        Assert.Equal(["tests failed"], result.OutputTail.Select(l => l.Trim()));
        Assert.Equal("verify.command exited with code 1.", Assert.Single(result.Errors).First.Message);
    }

    [Fact]
    public void A_solution_filter_resolves_to_the_solution_it_filters()
    {
        using var directory = new ScratchDirectory("slnf");
        directory.Write("slices/core.slnf", """{ "solution": { "path": "..\\App.sln", "projects": [] } }""");

        Assert.Equal("App.sln", VerifyRunner.UnderlyingSolution(directory.Path, "slices/core.slnf"));
        Assert.Equal("App.sln", VerifyRunner.UnderlyingSolution(directory.Path, "App.sln"));
        Assert.Null(VerifyRunner.UnderlyingSolution(directory.Path, "missing.slnf"));
    }

    private static async Task<VerifyResult> RunAsync(string root, WorkspaceModel model, DiagnosticBag diagnostics, bool recordBaseline = false) =>
        (await VerifyRunner.RunAsync(Request(root, model, diagnostics, ProcessRunner.Instance) with { RecordBaseline = recordBaseline }, TestContext.Current.CancellationToken))!;

    private static VerifyRequest Request(string root, WorkspaceModel model, DiagnosticBag diagnostics, IProcessRunner runner) => new()
    {
        RepositoryRoot = root,
        Model = model,
        Config = new VerifyConfig(),
        Mode = VerifyMode.Build,
        Projects = [.. model.Projects.Select(p => p.Id)],
        Everything = true,
        Scope = "every project",
        TargetFramework = "net10.0",
        Processes = runner,
        Diagnostics = diagnostics,
    };
}
