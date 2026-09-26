using System.Text.Json.Nodes;
using Offramp.Fixtures;
using Offramp.Core.Model;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

public sealed class PlanCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public PlanCommandTests()
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("versions"), _cli.Repo.Path);
        _cli.Repo.Write("offramp.yml", "version: 1\n");
        var model = FixtureModels.Load("versions");
        WorkspaceStore.Save(_cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = _cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(_cli.Repo.Path, _cli.Repo.Combine(".offramp"), model.Solution),
        });
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Json_envelope_carries_the_order_and_validates()
    {
        var run = await _cli.RunAsync("plan", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "plan");
        var order = JsonNode.Parse(run.Out)!["result"]!["order"]!.AsArray();
        Assert.Equal(
            ["src/Shared/Shared.csproj", "src/Modern.App/Modern.App.csproj", "src/Billing/Billing.csproj", "src/Customer.Api/Customer.Api.csproj", "src/Reporting/Reporting.csproj"],
            order.Select(e => e!["project"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Human_views_match_the_snapshots()
    {
        var plain = await _cli.RunAsync("plan");
        var waves = await _cli.RunAsync("plan", "--waves");

        Assert.Equal(0, plain.ExitCode);
        await Verify(Scrub.Text(plain.Out + "\n--- --waves\n" + waves.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task For_resolves_names_and_rejects_unknown_projects()
    {
        var reporting = await _cli.RunAsync("plan", "--for", "Reporting", "--json");
        var unknown = await _cli.RunAsync("plan", "--for", "Nope", "--json");

        Assert.Equal(["src/Reporting/Reporting.csproj"],
            JsonNode.Parse(reporting.Out)!["result"]!["order"]!.AsArray().Select(e => e!["project"]!.GetValue<string>()));
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains(JsonNode.Parse(unknown.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0021");
    }

    [Fact]
    public async Task Frontier_lists_what_can_be_ported_today()
    {
        var run = await _cli.RunAsync("plan", "--frontier", "--exclude-kind", "web");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("2 projects can be ported today.", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer.Api", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--exclude-kind", "gadget")]
    [InlineData("--for", "")]
    public async Task Invalid_options_are_usage_errors(string option, string value)
    {
        var run = await _cli.RunAsync("plan", option, value);

        Assert.Equal(2, run.ExitCode);
    }
}
