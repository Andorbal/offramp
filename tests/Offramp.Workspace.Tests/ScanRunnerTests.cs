using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Scanning;

namespace Offramp.Workspace.Tests;

/// <summary>Scan failures that need no real build: missing or unreadable inputs, and a build that misbehaves.</summary>
public sealed class ScanRunnerTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("scan");
    private readonly DiagnosticBag _diagnostics = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    [ProducesDiagnostic("OFR0004")]
    public async Task A_missing_log_is_an_environment_failure()
    {
        var outcome = await RunAsync(r => r with { BinlogPath = _repo.Combine("nope.binlog") });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        Assert.Equal("OFR0004", Assert.Single(_diagnostics.ToSortedList()).Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR0004")]
    public async Task An_unreadable_log_is_reported()
    {
        var path = _repo.Write("broken.binlog", "this is not a binary log");

        var outcome = await RunAsync(r => r with { BinlogPath = path });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        var diagnostic = Assert.Single(_diagnostics.ToSortedList());
        Assert.Equal("OFR0004", diagnostic.Code);
        Assert.Contains("broken.binlog", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR0003")]
    public async Task No_build_without_a_previous_log_is_reported()
    {
        var outcome = await RunAsync(r => r with { NoBuild = true });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        Assert.Equal("OFR0003", Assert.Single(_diagnostics.ToSortedList()).Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR0022")]
    public async Task A_repository_without_a_solution_is_reported()
    {
        _repo.Write("src/A/A.csproj", "<Project />");

        var outcome = await RunAsync();

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        Assert.Equal("OFR0022", Assert.Single(_diagnostics.ToSortedList()).Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR0020")]
    public async Task Several_solutions_without_a_choice_is_a_usage_error()
    {
        _repo.Write("a/One.sln", "");
        _repo.Write("b/Two.sln", "");

        var outcome = await RunAsync();

        Assert.Equal(ScanFailure.Usage, outcome.Failure);
        Assert.Equal("OFR0020", Assert.Single(_diagnostics.ToSortedList()).Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR0131")]
    public async Task A_build_that_times_out_is_reported()
    {
        _repo.Write("App.sln", "");
        var runner = new FakeProcessRunner().On("dotnet", ["build"], _ => new ProcessResult(-1, "", "") { TimedOut = true });

        var outcome = await RunAsync(r => r with { Processes = runner });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        var diagnostic = Assert.Single(_diagnostics.ToSortedList());
        Assert.Equal("OFR0131", diagnostic.Code);
        Assert.Contains("1800 seconds", diagnostic.Message, StringComparison.Ordinal);
        var build = Assert.Single(runner.Calls);
        Assert.Equal(TimeSpan.FromSeconds(1800), build.Timeout);
        Assert.Contains("-nodeReuse:false", build.Arguments);
        Assert.Contains("--no-incremental", build.Arguments);
    }

    [Fact]
    [ProducesDiagnostic("OFR0130")]
    public async Task A_build_that_writes_no_log_is_reported()
    {
        _repo.Write("App.sln", "");
        var runner = new FakeProcessRunner().On("dotnet", ["build"], 1, "", "MSBUILD : error MSB1009: Project file does not exist.\n");

        var outcome = await RunAsync(r => r with { Processes = runner });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        var diagnostic = Assert.Single(_diagnostics.ToSortedList());
        Assert.Equal("OFR0130", diagnostic.Code);
        Assert.Contains("MSB1009", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR0010")]
    public async Task A_missing_dotnet_is_reported()
    {
        _repo.Write("App.sln", "");

        var outcome = await RunAsync(r => r with { Processes = new FakeProcessRunner() });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        Assert.Equal("OFR0010", Assert.Single(_diagnostics.ToSortedList()).Code);
    }

    [Fact]
    public async Task Msbuild_rebuilds_with_restore_the_configuration_and_the_properties()
    {
        _repo.Write("App.sln", "");
        var msbuild = _repo.Write("tools/MSBuild.exe", "");
        var runner = new FakeProcessRunner().On(spec => spec.FileName == msbuild, _ => new ProcessResult(1, "", "MSB1009: Project file does not exist."));
        var config = MsbuildConfig("tools/MSBuild.exe") with
        {
            Verify = new VerifyConfig
            {
                Configuration = "Release",
                Properties = new(StringComparer.Ordinal) { ["GenerateSerializationAssemblies"] = "On", ["RestorePackagesConfig"] = "false" },
            },
        };

        var outcome = await RunAsync(r => r with { Processes = runner, Config = config });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        Assert.Equal("OFR0130", Assert.Single(_diagnostics.ToSortedList()).Code);
        var build = Assert.Single(runner.Calls);
        Assert.Equal(msbuild, build.FileName);
        Assert.Equal(
            [
                _repo.Combine("App.sln"), "-restore", "-t:Rebuild", "-m",
                "-bl:" + _repo.Combine(".offramp", "msbuild.binlog"), "-p:Configuration=Release",
                "-nologo", "-v:minimal", "-clp:NoSummary", "-nodeReuse:false",
                "-p:RestorePackagesConfig=true",
                "-p:GenerateSerializationAssemblies=On", "-p:RestorePackagesConfig=false",
            ],
            build.Arguments);
        Assert.Equal(_repo.Path, build.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(1800), build.Timeout);
    }

    [Fact]
    public async Task Msbuild_is_found_in_a_build_tools_installation_folder()
    {
        _repo.Write("App.sln", "");
        var msbuild = _repo.Write("BuildTools/MSBuild/Current/Bin/MSBuild.exe", "");
        _repo.Write("BuildTools/MSBuild/15.0/Bin/MSBuild.exe", "");
        var runner = new FakeProcessRunner().On(_ => true, _ => new ProcessResult(1, "", ""));

        await RunAsync(r => r with { Processes = runner, Config = MsbuildConfig("BuildTools") });

        Assert.Equal(msbuild, Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    [ProducesDiagnostic("OFR0017")]
    public async Task A_configured_msbuild_that_does_not_exist_is_reported_without_building()
    {
        _repo.Write("App.sln", "");
        _repo.Write("NotBuildTools/readme.txt", "");
        var runner = new FakeProcessRunner();

        var outcome = await RunAsync(r => r with { Processes = runner, Config = MsbuildConfig("NotBuildTools") });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        var diagnostic = Assert.Single(_diagnostics.ToSortedList());
        Assert.Equal("OFR0017", diagnostic.Code);
        Assert.Contains("'NotBuildTools' is neither MSBuild.exe nor a folder holding it", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("--msbuild-path", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Msbuild_comes_from_the_installation_vswhere_reports()
    {
        _repo.Write("App.sln", "");
        var msbuild = _repo.Write("VS/2022/Enterprise/MSBuild/Current/Bin/MSBuild.exe", "");
        var vswhere = _repo.Combine("Program Files (x86)", "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var runner = new FakeProcessRunner()
            .On(spec => spec.FileName == vswhere, _ => new ProcessResult(0, msbuild + "\r\n", ""))
            .On(spec => spec.FileName == msbuild, _ => new ProcessResult(1, "", ""));

        await RunAsync(r => r with
        {
            Processes = runner,
            Config = MsbuildConfig(null),
            Environment = new Dictionary<string, string> { ["ProgramFiles(x86)"] = _repo.Combine("Program Files (x86)") },
        });

        Assert.Collection(runner.Calls,
            query => Assert.Equal(
                ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                query.Arguments),
            build => Assert.Equal(msbuild, build.FileName));
    }

    [Fact]
    public async Task A_developer_command_prompt_installation_comes_before_vswhere()
    {
        _repo.Write("App.sln", "");
        var msbuild = _repo.Write("VS/2019/BuildTools/MSBuild/Current/Bin/MSBuild.exe", "");
        var runner = new FakeProcessRunner().On(_ => true, _ => new ProcessResult(1, "", ""));

        await RunAsync(r => r with
        {
            Processes = runner,
            Config = MsbuildConfig(null),
            Environment = new Dictionary<string, string>
            {
                ["VSINSTALLDIR"] = _repo.Combine("VS", "2019", "BuildTools") + Path.DirectorySeparatorChar,
                ["ProgramFiles(x86)"] = _repo.Combine("Program Files (x86)"),
            },
        });

        Assert.Equal(msbuild, Assert.Single(runner.Calls).FileName);
    }

    [Theory]
    [InlineData(true, "vswhere reports no Visual Studio or Build Tools installation with the MSBuild component")]
    [InlineData(false, "no installation to look in here")]
    [ProducesDiagnostic("OFR0017")]
    public async Task Msbuild_that_cannot_be_found_is_reported(bool windows, string reason)
    {
        _repo.Write("App.sln", "");
        var runner = new FakeProcessRunner().On(spec => spec.FileName.EndsWith("vswhere.exe", StringComparison.Ordinal), _ => new ProcessResult(0, "", ""));
        var environment = windows
            ? new Dictionary<string, string> { ["ProgramFiles(x86)"] = _repo.Combine("Program Files (x86)") }
            : [];

        var outcome = await RunAsync(r => r with { Processes = runner, Config = MsbuildConfig(null), Environment = environment });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        var diagnostic = Assert.Single(_diagnostics.ToSortedList());
        Assert.Equal("OFR0017", diagnostic.Code);
        Assert.Contains(reason, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(windows ? 1 : 0, runner.Calls.Count);
    }

    [Fact]
    [ProducesDiagnostic("OFR0017")]
    public async Task Msbuild_that_cannot_be_started_is_reported()
    {
        _repo.Write("App.sln", "");
        var msbuild = _repo.Write("tools/MSBuild.exe", "");

        var outcome = await RunAsync(r => r with { Processes = new FakeProcessRunner(), Config = MsbuildConfig(msbuild) });

        Assert.Equal(ScanFailure.Environment, outcome.Failure);
        var diagnostic = Assert.Single(_diagnostics.ToSortedList());
        Assert.Equal("OFR0017", diagnostic.Code);
        Assert.Contains("could not be started", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR0102")]
    public async Task A_project_no_kind_rule_matches_is_reported()
    {
        _repo.Write("Directory.Build.props", "<Project />");
        _repo.Write("Directory.Build.targets", "<Project />");
        _repo.Write("Odd/Odd.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Module</OutputType>
              </PropertyGroup>
            </Project>
            """);
        _repo.Write("Odd/Code.cs", "namespace Odd; internal static class Code { }\n");
        var solution = await RunDotnetAsync(_repo.Path, "new", "sln", "--name", "Odd", "--format", "slnx");
        Assert.True(solution.Succeeded, solution.StandardError);
        var add = await RunDotnetAsync(_repo.Path, "sln", "Odd.slnx", "add", "Odd/Odd.csproj");
        Assert.True(add.Succeeded, add.StandardError);

        var outcome = await RunAsync(r => r with { Processes = ProcessRunner.Instance });

        Assert.Equal(ScanFailure.None, outcome.Failure);
        var diagnostic = Assert.Single(outcome.Model!.Diagnostics, d => d.Code == "OFR0102");
        Assert.Equal("Odd/Odd.csproj", diagnostic.Project);
        Assert.Equal(["Odd/Odd.csproj"], outcome.Result!.Unrecognized);
    }

    private static OfframpConfig MsbuildConfig(string? path) =>
        new() { Scan = new ScanConfig { Builder = ScanConfig.Msbuild, MsbuildPath = path } };

    private Task<ScanOutcome> RunAsync(Func<ScanRequest, ScanRequest>? customize = null)
    {
        var request = ScannedFixtures.Request(_repo.Path, _diagnostics, new FakeProcessRunner());
        return ScanRunner.RunAsync(customize?.Invoke(request) ?? request, TestContext.Current.CancellationToken);
    }

    private static Task<ProcessResult> RunDotnetAsync(string directory, params string[] arguments) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", arguments) { WorkingDirectory = directory }, TestContext.Current.CancellationToken);
}
