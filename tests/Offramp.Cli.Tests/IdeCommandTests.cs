using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Offramp.Cli.Commands;
using Offramp.Fixtures;
using Offramp.Ide.Lsp;

namespace Offramp.Cli.Tests;

/// <summary>docs/spec/commands/ide.md: <c>ide check</c>'s contract and <c>ide serve</c> wired to the CLI's own scan.</summary>
public sealed class IdeCommandTests
{
    private const string TaxRule = "namespace Foo.Pricing\n{\n    public sealed class TaxRule\n    {\n        public decimal Apply(decimal amount, decimal rate) => amount * (1 + rate);\n    }\n}\n";

    [Fact]
    public async Task Check_json_validates_and_matches_the_snapshot()
    {
        var fixture = await ScannedFixtures.ScanAsync("ide-counterpart");
        using var repository = fixture.Repository;
        repository.Directory.Write("src/Foo/Pricing/TaxRule.cs", TaxRule);
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var json = await cli.RunAsync("ide", "check", "--json");
        var human = await cli.RunAsync("ide", "check");

        Assert.Equal(0, json.ExitCode);
        SchemaAssert.ValidEnvelope(json.Out, "ide-check");
        var commit = JsonNode.Parse(json.Out)!["result"]!["base"]!["commit"]!.GetValue<string>();
        await Verify(Scrub.Envelope(json.Out, fixture.Root).Replace(commit, "{Commit}", StringComparison.Ordinal), extension: "json");
        await Verify(Scrub.Text(human.Out, fixture.Root).Replace(commit[..8], "{Commit}", StringComparison.Ordinal), extension: "txt").UseMethodName("Check_human_view_matches_the_snapshot");
    }

    [Fact]
    [ProducesDiagnostic("OFR6006")]
    public async Task Files_named_on_the_command_line_are_reported_even_when_unchanged_or_outside_every_project()
    {
        var fixture = await ScannedFixtures.GetAsync("ide-counterpart");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();
        var loose = fixture.Repository.Directory.Write("tools/Loose.cs", "class Loose { }\n");

        try
        {
            var run = await cli.RunAsync("ide", "check", "--file", "src/Foo/Pricing/PriceCalculator.cs", "--file", "tools/Loose.cs", "--scope", "all", "--json");

            Assert.Equal(0, run.ExitCode);
            var result = JsonNode.Parse(run.Out)!;
            var files = result["result"]!["files"]!.AsArray();
            Assert.Equal(["src/Foo/Pricing/PriceCalculator.cs", "tools/Loose.cs"], files.Select(f => f!["file"]!.GetValue<string>()));
            Assert.True(files[0]!["moves"]![0]!["movable"]!.GetValue<bool>());
            Assert.Null(files[1]!["project"]);
            Assert.Contains(result["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR6006");
        }
        finally
        {
            File.Delete(loose);
        }
    }

    [Fact]
    public void Serve_accepts_the_stdio_flag_language_clients_pass()
    {
        using var cli = new CliHarness();
        var root = OfframpCli.BuildRoot(cli.Host(new StringWriter(), new StringWriter()));

        Assert.Empty(root.Parse(["ide", "serve", "--stdio"]).Errors);
        Assert.NotEmpty(root.Parse(["ide", "serve", "--tcp"]).Errors);
    }

    [Fact]
    public async Task Serve_offers_to_scan_a_repository_without_a_model_and_then_reports_on_it()
    {
        using var repository = await FixtureRepository.CreateAsync("ide-counterpart");
        Directory.CreateDirectory(repository.Directory.Combine(".offramp"));
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var host = cli.Host(new StringWriter(), new StringWriter());
        var toServer = new Pipe();
        var toClient = new Pipe();
        using var connection = new LspConnection(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var server = OfframpLanguageServer.RunAsync(toServer.Reader.AsStream(), toClient.Writer.AsStream(), IdeCommands.ServerOptions(host) with { Debounce = TimeSpan.Zero }, stop.Token);
        var statuses = new List<JsonNode>();
        string? asked = null;

        await connection.WriteAsync(new JsonObject { ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["rootUri"] = new Uri(repository.Path).AbsoluteUri } }, stop.Token);
        await connection.WriteAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, stop.Token);
        while (await connection.ReadAsync(stop.Token) is { } message)
        {
            switch ((string?)message["method"])
            {
                case "window/showMessageRequest":
                    asked = (string?)message["params"]!["message"];
                    await connection.WriteAsync(new JsonObject { ["id"] = message["id"]!.DeepClone(), ["result"] = message["params"]!["actions"]![0]!.DeepClone() }, stop.Token);
                    break;
                case "offramp/status":
                    statuses.Add(message["params"]!.DeepClone());
                    break;
            }

            if (statuses.Count == 2)
            {
                break;
            }
        }

        await connection.WriteAsync(new JsonObject { ["id"] = 2, ["method"] = "shutdown" }, stop.Token);
        await connection.WriteAsync(new JsonObject { ["method"] = "exit" }, stop.Token);

        Assert.Contains("Scan now?", asked, StringComparison.Ordinal);
        Assert.Equal(["missing", "fresh"], statuses.Select(s => (string)s["model"]!));
        Assert.Equal("src/Foo/Foo.csproj", (string?)statuses[1]["counterparts"]![0]!["project"]);
        Assert.True(repository.Directory.Exists(".offramp/workspace.json"));
        Assert.Equal(0, await server.WaitAsync(stop.Token));
    }
}
