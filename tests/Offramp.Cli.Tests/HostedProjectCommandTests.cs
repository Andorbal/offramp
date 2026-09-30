using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary>
/// SmartStoreNET P1 #5 and DotNetNuke's "every web project is an application" on the <c>plugin-host</c> fixture: a site,
/// an area nested in it that builds into its <c>bin/</c>, a plugin that builds into its <c>Plugins/</c> folder, and a
/// module that copies its assembly into its <c>bin/</c> are one application (ADR 0055).
/// </summary>
public sealed class HostedProjectCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR0204")]
    public async Task Report_plan_and_inventory_treat_the_site_as_the_application()
    {
        var fixture = await ScannedFixtures.GetAsync("plugin-host");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var report = await cli.RunAsync("report", "--format", "json", "--json");
        var plan = await cli.RunAsync("plan", "--for", "Site", "--json");
        var inventory = await cli.RunAsync("web", "inventory", "--project", "Tax", "--json");

        Assert.Equal((0, 0, 0), (report.ExitCode, plan.ExitCode, inventory.ExitCode));
        SchemaAssert.ValidEnvelope(report.Out, "report");
        SchemaAssert.ValidEnvelope(inventory.Out, "web-inventory");
        var application = Assert.Single(JsonNode.Parse(report.Out)!["result"]!["report"]!["applications"]!.AsArray())!;
        Assert.Equal("src/Site/Site.csproj", application["project"]!.GetValue<string>());
        Assert.Equal(["src/Modules/Html/Html.csproj", "src/Plugins/Tax/Tax.csproj", "src/Site/Admin/Admin.csproj"], application["hosted"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Equal(3, Codes(report.Out).Count(c => c == "OFR0204"));
        Assert.Equal(
            ["src/Core/Core.csproj", "src/Modules/Html/Html.csproj", "src/Plugins/Tax/Tax.csproj", "src/Site/Admin/Admin.csproj", "src/Site/Site.csproj"],
            JsonNode.Parse(plan.Out)!["result"]!["order"]!.AsArray().Select(e => e!["project"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var result = JsonNode.Parse(inventory.Out)!["result"]!;
        Assert.Equal(("src/Site/Site.csproj", "http://localhost:52001/"), (result["hostedBy"]!.GetValue<string>(), result["url"]!.GetValue<string>()));
        Assert.Contains("OFR0204", Codes(inventory.Out));
    }

    [Fact]
    [ProducesDiagnostic("OFR0205")]
    public async Task Web_scaffold_of_a_plugin_proxies_to_the_site()
    {
        var fixture = await ScannedFixtures.GetAsync("plugin-host");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("web", "scaffold", "--project", "Tax", "--new", "src/Tax.Core", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "web-scaffold");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal(("http://localhost:52001/", "src/Site/Site.csproj"), (result["legacyUrl"]!.GetValue<string>(), result["hostedBy"]!.GetValue<string>()));
        Assert.Contains("OFR0205", Codes(run.Out));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "src", "Tax.Core")));
    }

    private static List<string> Codes(string envelope) =>
        [.. JsonNode.Parse(envelope)!["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>())];
}
