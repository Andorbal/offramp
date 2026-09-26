using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

/// <summary>
/// The <c>--llm</c> gates against a stubbed OpenAI-compatible server (ROADMAP M13): each
/// permitted use asks, and what the model produced is marked <c>source: llm</c>; without
/// <c>--llm</c> nothing is asked; a failed call or an unusable answer falls back (OFR9001).
/// </summary>
public sealed class LlmCommandTests
{
    private const string SeamsConfig = "version: 1\nseams:\n  unportableSources: [ list ]\n  unportableSymbols: [ System.DirectoryServices ]\n";

    [Fact]
    public async Task Naming_and_summarizing_ask_the_model_and_mark_what_it_produced()
    {
        var fixture = await ScannedFixtures.ScanAsync("seams");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", SeamsConfig + "llm:\n  uses: [ naming, summarizing ]\n");
        await using var llm = new StubLlmServer(Answer);
        using var cli = new CliHarness(repository.Directory);
        llm.Configure(cli.Environment);

        var seams = await cli.RunAsync("seams", "--project", "Accounts", "--llm", "--json");
        var extract = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--type", "Accounts.Directory.DirectoryLookup", "--llm", "--json");
        var report = await cli.RunAsync("report", "--format", "markdown", "--llm", "--json");

        Assert.Equal(0, seams.ExitCode);
        SchemaAssert.ValidEnvelope(seams.Out, "seams");
        var seam = JsonNode.Parse(seams.Out)!["result"]!["seams"]![0]!;
        Assert.Equal("IUserDirectory", seam["proposedInterface"]!.GetValue<string>());
        Assert.Equal("llm", seam["source"]!.GetValue<string>());
        SchemaAssert.ValidEnvelope(extract.Out, "extract-interface");
        var extracted = JsonNode.Parse(extract.Out)!["result"]!;
        Assert.Equal(("Accounts.Directory.IUserDirectory", "llm"), (extracted["interface"]!.GetValue<string>(), extracted["source"]!.GetValue<string>()));
        SchemaAssert.ValidEnvelope(report.Out, "report");
        var summary = JsonNode.Parse(report.Out)!["result"]!["summary"]!;
        Assert.Equal(("The migration is well under way.", "llm"), (summary["text"]!.GetValue<string>(), summary["source"]!.GetValue<string>()));
        Assert.Contains("_Summary written by a language model", JsonNode.Parse(report.Out)!["result"]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // The model sees signatures and names, never code.
        var naming = llm.Prompts.First(p => p.Contains("DirectoryLookup", StringComparison.Ordinal));
        Assert.Contains("FindUser", naming, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectoryEntry(", naming, StringComparison.Ordinal);
        Assert.DoesNotContain("return ", naming, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR9001")]
    public async Task Without_llm_nothing_is_asked_and_unusable_answers_fall_back()
    {
        var fixture = await ScannedFixtures.ScanAsync("seams");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", SeamsConfig + "llm:\n  enabled: true\n");
        await using var nonsense = new StubLlmServer(_ => """{"name":"not a name"}""");
        using var cli = new CliHarness(repository.Directory);
        nonsense.Configure(cli.Environment);

        var off = await cli.RunAsync("seams", "--project", "Accounts", "--no-llm", "--json");
        Assert.Empty(nonsense.Requests);
        var unusable = await cli.RunAsync("seams", "--project", "Accounts", "--json");
        cli.Environment["OFFRAMP_LLM_URL"] = "http://127.0.0.1:9/v1";
        cli.Environment["OFFRAMP_LLM_MODEL"] = "any";
        var unreachable = await cli.RunAsync("seams", "--project", "Accounts", "--json");

        foreach (var run in new[] { off, unusable, unreachable })
        {
            Assert.Equal(0, run.ExitCode);
            var seam = JsonNode.Parse(run.Out)!["result"]!["seams"]![0]!;
            Assert.Equal("IDirectoryLookup", seam["proposedInterface"]!.GetValue<string>());
            Assert.Null(seam["source"]);
        }

        Assert.DoesNotContain("OFR9001", off.Out, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"OFR9001\"", unusable.Out, StringComparison.Ordinal);
        Assert.Contains("not usable", unusable.Out, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"OFR9001\"", unreachable.Out, StringComparison.Ordinal);
        Assert.NotEmpty(nonsense.Requests);
    }

    [Fact]
    public async Task Classifying_adds_evidence_to_low_confidence_dead_code_and_never_changes_the_confidence()
    {
        var fixture = await ScannedFixtures.ScanAsync("dead-code");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\nllm:\n  uses: [ classifying ]\n");
        await using var llm = new StubLlmServer(Answer);
        using var cli = new CliHarness(repository.Directory);
        llm.Configure(cli.Environment);

        var plain = await cli.RunAsync("audit", "dead-code", "--json");
        var classified = await cli.RunAsync("audit", "dead-code", "--llm", "--json");

        SchemaAssert.ValidEnvelope(classified.Out, "dead-code");
        var before = Candidates(plain.Out);
        var after = Candidates(classified.Out);
        Assert.Equal(before.Select(c => (c["symbol"]!.GetValue<string>(), c["confidence"]!.GetValue<string>())), after.Select(c => (c["symbol"]!.GetValue<string>(), c["confidence"]!.GetValue<string>())));
        var marked = after.Where(c => c["source"] is not null).ToList();
        Assert.NotEmpty(marked);
        Assert.All(marked, c => Assert.Equal("low", c["confidence"]!.GetValue<string>()));
        Assert.All(marked, c => Assert.Contains(c["evidence"]!.AsArray(), e => e!.GetValue<string>().StartsWith("the model judges the name likely used by convention", StringComparison.Ordinal)));
        Assert.Equal(JsonNode.Parse(plain.Out)!["result"]!["summary"]!.ToJsonString(), JsonNode.Parse(classified.Out)!["result"]!["summary"]!.ToJsonString());
    }

    [Fact]
    public async Task Ranking_chooses_among_the_matching_package_map_entries()
    {
        using var cli = new CliHarness();
        VersionsFeed.WriteFolderFeed(cli.Repo.Combine("recorded-feed"));
        cli.Repo.Write("offramp.yml", "version: 1\ndeps:\n  feeds: [ recorded-feed ]\n  packageMap:\n    - package: EntityFramework\n      replacement: Microsoft.EntityFrameworkCore (a rewrite)\nllm:\n  uses: [ ranking ]\n");
        var model = FixtureModels.Load("versions");
        WorkspaceStore.Save(cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(cli.Repo.Path, cli.Repo.Combine(".offramp"), model.Solution),
        });
        await using var llm = new StubLlmServer(Answer);
        llm.Configure(cli.Environment);

        var rules = await cli.RunAsync("deps", "audit", "--package", "EntityFramework", "--json");
        var ranked = await cli.RunAsync("deps", "audit", "--package", "EntityFramework", "--llm", "--json");

        SchemaAssert.ValidEnvelope(ranked.Out, "deps-audit");
        Assert.Equal(("Microsoft.EntityFrameworkCore (a rewrite)", "offramp.yml"), Replacement(rules.Out));
        Assert.Equal(("EntityFramework 6.4 or later (supports netstandard2.1), or Microsoft.EntityFrameworkCore", "llm"), Replacement(ranked.Out));
        var prompt = Assert.Single(llm.Prompts);
        Assert.Contains("1. Microsoft.EntityFrameworkCore (a rewrite)", prompt, StringComparison.Ordinal);
    }

    /// <summary>What the stub model says: a name, the second candidate, conventions all round, or a sentence.</summary>
    private static string Answer(JsonNode request)
    {
        var schema = request["response_format"]?["json_schema"]?["schema"]?["properties"];
        if (schema?["name"] is not null)
        {
            return """{"name":"IUserDirectory"}""";
        }

        if (schema?["choice"] is not null)
        {
            return """{"choice":2}""";
        }

        if (schema?["items"] is not null)
        {
            var prompt = request["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
            var symbols = prompt.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..l.IndexOf(" (", StringComparison.Ordinal)]);
            return new JsonObject { ["items"] = new JsonArray([.. symbols.Select(s => (JsonNode)new JsonObject { ["symbol"] = s, ["convention"] = true, ["reason"] = "a DI-scanned name" })]) }.ToJsonString();
        }

        return "The migration is well under way.";
    }

    private static List<JsonNode> Candidates(string envelope) =>
        [.. JsonNode.Parse(envelope)!["result"]!["projects"]!.AsArray().SelectMany(p => p!["candidates"]!.AsArray()).Select(c => c!)];

    private static (string Replacement, string Source) Replacement(string envelope)
    {
        var replacement = JsonNode.Parse(envelope)!["result"]!["packages"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "EntityFramework")!["replacement"]!;
        return (replacement["replacement"]!.GetValue<string>(), replacement["source"]!.GetValue<string>());
    }
}
