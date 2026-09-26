using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>move extract</c> on the <c>move-cases</c> fixture: a new project, then a verified pure move into it.</summary>
public sealed class MoveExtractCommandTests
{
    [Fact]
    public async Task Extract_creates_the_project_moves_the_types_and_verifies()
    {
        var fixture = await ScannedFixtures.ScanAsync("move-cases");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        string[] arguments = ["move", "extract", "--from", "Legacy", "--types", "Legacy.Clean.Money,Invoice", "--files", "src/Legacy/Json/*.cs", "--new", "Legacy.Domain", "--tfm", "net48;netstandard2.0"];
        var hashes = new[] { "src/Legacy/Clean/Money.cs", "src/Legacy/Json/Serializer.cs", "src/Legacy/Partial/Invoice.cs" }
            .ToDictionary(f => f, f => ContentHash.Sha256File(repository.Directory.Combine(f.Split('/'))));

        var dryRun = await cli.RunAsync([.. arguments, "--json"]);

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "move-extract");
        Assert.False(repository.Directory.Exists("src/Legacy.Domain"));
        await Verify(Scrub.Envelope(ScrubPlan(dryRun.Out), repository.Path), extension: "json");

        var applied = await cli.RunAsync([.. arguments, "--apply", "--json"]);

        Assert.True(applied.ExitCode == 0, applied.Out);
        SchemaAssert.ValidEnvelope(applied.Out, "move-extract");
        var apply = JsonNode.Parse(applied.Out)!["result"]!["apply"]!;
        Assert.Equal("passed", Assert.Single(apply["verifications"]!.AsArray())!["status"]!.GetValue<string>());
        Assert.Equal(
            string.Join('\n',
                "R100\tsrc/Legacy/Clean/Money.cs\tsrc/Legacy.Domain/Clean/Money.cs",
                "R100\tsrc/Legacy/Json/Serializer.cs\tsrc/Legacy.Domain/Json/Serializer.cs",
                "R100\tsrc/Legacy/Partial/Invoice.Totals.cs\tsrc/Legacy.Domain/Partial/Invoice.Totals.cs",
                "R100\tsrc/Legacy/Partial/Invoice.cs\tsrc/Legacy.Domain/Partial/Invoice.cs"),
            (await repository.GitAsync("diff", "--cached", "-M100%", "--name-status")).StandardOutput.Trim());
        foreach (var (file, hash) in hashes)
        {
            Assert.Equal(hash, ContentHash.Sha256File(repository.Directory.Combine(file.Replace("src/Legacy/", "src/Legacy.Domain/", StringComparison.Ordinal).Split('/'))));
        }

        var project = repository.Directory.Read("src/Legacy.Domain/Legacy.Domain.csproj");
        Assert.Contains("<TargetFrameworks>net48;netstandard2.0</TargetFrameworks>", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", project, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"..\\Legacy.Domain\\Legacy.Domain.csproj\" />", repository.Directory.Read("src/Legacy/Legacy.csproj"), StringComparison.Ordinal);
        Assert.Contains("src/Legacy.Domain/Legacy.Domain.csproj", repository.Directory.Read("MoveCases.slnx"), StringComparison.Ordinal);

        var rollback = await cli.RunAsync("move", "rollback", "--journal", apply["journal"]!.GetValue<string>(), "--json");

        Assert.Equal(0, rollback.ExitCode);
        Assert.False(repository.Directory.Exists("src/Legacy.Domain/Legacy.Domain.csproj"));
        Assert.Equal(hashes["src/Legacy/Clean/Money.cs"], ContentHash.Sha256File(repository.Directory.Combine("src", "Legacy", "Clean", "Money.cs")));
    }

    /// <summary>The plan's workspace hash (the model records the OS) and file hashes.</summary>
    private static string ScrubPlan(string json)
    {
        var node = JsonNode.Parse(json)!;
        var plan = node["result"]!["plan"]!;
        plan["workspaceHash"] = "sha256:{Hash}";
        foreach (var move in plan["moves"]!.AsArray())
        {
            move!["sha256"] = "{Sha256}";
        }

        return node.ToJsonString();
    }

    [Fact]
    public async Task A_type_that_does_not_compile_in_the_new_project_stays_and_nothing_is_created()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("move", "extract", "--from", "Legacy", "--types", "LinkBuilder", "--new", "Legacy.Portable", "--tfm", "netstandard2.0", "--apply", "--json");

        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Empty(result["plan"]!["moves"]!.AsArray());
        Assert.Equal("OFR2103", Assert.Single(result["plan"]!["excluded"]!.AsArray())!["code"]!.GetValue<string>());
        Assert.Null(result["apply"]);
        Assert.False(fixture.Repository.Directory.Exists("src/Legacy.Portable"));
    }

    [Fact]
    [ProducesDiagnostic("OFR2006")]
    [ProducesDiagnostic("OFR2007")]
    [ProducesDiagnostic("OFR2008")]
    public async Task Unknown_types_patterns_existing_folders_and_unknown_targets_are_refused()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var type = await cli.RunAsync("move", "extract", "--from", "Legacy", "--types", "Legacy.Nope", "--new", "Legacy.Domain", "--json");
        var pattern = await cli.RunAsync("move", "extract", "--from", "Legacy", "--files", "src/Legacy/Nope/*.cs", "--new", "Legacy.Domain", "--json");
        var exists = await cli.RunAsync("move", "extract", "--from", "Legacy", "--types", "Money", "--new", "Core", "--json");
        var target = await cli.RunAsync("move", "extract", "--from", "Legacy", "--types", "Money", "--new", "Legacy.Domain", "--tfm", "net99.0", "--json");

        Assert.Equal(2, type.ExitCode);
        Assert.Contains("\"code\": \"OFR2006\"", type.Out, StringComparison.Ordinal);
        Assert.Equal(2, pattern.ExitCode);
        Assert.Contains("\"code\": \"OFR2006\"", pattern.Out, StringComparison.Ordinal);
        Assert.Equal(2, exists.ExitCode);
        Assert.Contains("\"code\": \"OFR2007\"", exists.Out, StringComparison.Ordinal);
        Assert.Equal(2, target.ExitCode);
        Assert.Contains("\"code\": \"OFR2008\"", target.Out, StringComparison.Ordinal);
    }
}
