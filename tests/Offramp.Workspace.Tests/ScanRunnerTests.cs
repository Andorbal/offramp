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

    private Task<ScanOutcome> RunAsync(Func<ScanRequest, ScanRequest>? customize = null)
    {
        var request = ScannedFixtures.Request(_repo.Path, _diagnostics, new FakeProcessRunner());
        return ScanRunner.RunAsync(customize?.Invoke(request) ?? request, TestContext.Current.CancellationToken);
    }

    private static Task<ProcessResult> RunDotnetAsync(string directory, params string[] arguments) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", arguments) { WorkingDirectory = directory }, TestContext.Current.CancellationToken);
}
