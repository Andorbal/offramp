using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary>ROADMAP M5 acceptance for <c>move plan</c> and <c>move apply</c> through the CLI.</summary>
public sealed class MovePlanCommandTests
{
    private static readonly string[] Asked = ["--files", "src/Legacy/Clean/*.cs", "src/Legacy/Orders/*.cs", "src/Legacy/Resources/Strings.Designer.cs"];

    [Fact]
    public async Task Plan_is_deterministic_reviewable_and_changes_nothing()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();
        var before = MoveCommandTests.Tree(fixture.Root);

        var first = await cli.RunAsync(["move", "plan", "--from", "Legacy", "--to", "Core", .. Asked, "--json"]);
        var second = await cli.RunAsync(["move", "plan", "--from", "Legacy", "--to", "Core", .. Asked, "--json"]);
        var human = await cli.RunAsync(["move", "plan", "--from", "Legacy", "--to", "Core", .. Asked]);

        Assert.Equal(0, first.ExitCode);
        SchemaAssert.ValidEnvelope(first.Out, "move-plan-result");
        var result = JsonNode.Parse(first.Out)!["result"]!;
        Assert.Equal(result.ToJsonString(), JsonNode.Parse(second.Out)!["result"]!.ToJsonString());
        Assert.Equal(
            ["src/Legacy/Clean/Money.cs", "src/Legacy/Orders/OrderMapper.cs", "src/Legacy/Resources/Strings.Designer.cs", "src/Legacy/Resources/Strings.resx"],
            result["plan"]!["moves"]!.AsArray().Select(m => m!["file"]!.GetValue<string>()));
        Assert.Null(result["output"]);
        Assert.Equal(before, MoveCommandTests.Tree(fixture.Root));
        await Verify(Scrub.Text(human.Out, fixture.Root), extension: "txt");
    }

    [Fact]
    [ProducesDiagnostic("OFR2004")]
    [ProducesDiagnostic("OFR2005")]
    public async Task Unknown_files_and_plans_are_usage_errors()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var nothing = await cli.RunAsync("move", "plan", "--from", "Legacy", "--to", "Core", "--files", "src/Legacy/Nope/*.cs", "--json");
        var both = await cli.RunAsync("move", "plan", "--from", "Legacy", "--to", "Core", "--files", "a.cs", "--all", "--json");
        var missing = await cli.RunAsync("move", "apply", "--plan", "no-such-plan.json", "--json");

        Assert.Equal(2, nothing.ExitCode);
        Assert.Contains(JsonNode.Parse(nothing.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2004");
        Assert.Equal(2, both.ExitCode);
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains(JsonNode.Parse(missing.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2005");
    }

    [Fact]
    public async Task Applying_a_plan_stages_pure_renames_verifies_and_rolls_back_exactly()
    {
        var fixture = await ScannedFixtures.ScanAsync("move-cases");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var before = MoveCommandTests.Tree(fixture.Root);
        var hashes = new[] { "src/Legacy/Clean/Money.cs", "src/Legacy/Resources/Strings.resx" }
            .ToDictionary(f => f, f => ContentHash.Sha256File(repository.Directory.Combine(f.Split('/'))));

        var plan = await cli.RunAsync(["move", "plan", "--from", "Legacy", "--to", "Core", .. Asked, "--out", "move-plan.json", "--json"]);
        Assert.Equal(0, plan.ExitCode);
        SchemaAssert.Valid("move-plan", repository.Directory.Read("move-plan.json"));
        Assert.Equal("move-plan.json", JsonNode.Parse(plan.Out)!["result"]!["output"]!.GetValue<string>());

        var run = await cli.RunAsync("move", "apply", "--plan", "move-plan.json", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "move-apply");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.True(result["applied"]!.GetValue<bool>());
        Assert.Equal("passed", Assert.Single(result["verifications"]!.AsArray())!["status"]!.GetValue<string>());
        Assert.Equal(
            string.Join('\n',
                "R100\tsrc/Legacy/Clean/Money.cs\tsrc/Core/Clean/Money.cs",
                "R100\tsrc/Legacy/Orders/OrderMapper.cs\tsrc/Core/Orders/OrderMapper.cs",
                "R100\tsrc/Legacy/Resources/Strings.Designer.cs\tsrc/Core/Resources/Strings.Designer.cs",
                "R100\tsrc/Legacy/Resources/Strings.resx\tsrc/Core/Resources/Strings.resx"),
            (await repository.GitAsync("diff", "--cached", "-M100%", "--name-status")).StandardOutput.Trim());
        Assert.Equal(hashes["src/Legacy/Clean/Money.cs"], ContentHash.Sha256File(repository.Directory.Combine("src", "Core", "Clean", "Money.cs")));
        Assert.Equal(hashes["src/Legacy/Resources/Strings.resx"], ContentHash.Sha256File(repository.Directory.Combine("src", "Core", "Resources", "Strings.resx")));
        Assert.Equal(
            [" M src/Core/Core.csproj", " M src/Legacy/Legacy.csproj"],
            (await repository.GitAsync("status", "--porcelain", "--untracked-files=no", "--", "src")).StandardOutput.Split('\n').Where(l => l.StartsWith(' ')));

        var rollback = await cli.RunAsync("move", "rollback", "--journal", result["journal"]!.GetValue<string>(), "--json");

        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, MoveCommandTests.Tree(fixture.Root).Where(f => !f.StartsWith("move-plan.json ", StringComparison.Ordinal)));
    }

    [Fact]
    [ProducesDiagnostic("OFR2150")]
    public async Task A_stale_plan_is_refused_and_changed_files_stay_with_what_needs_them()
    {
        var fixture = await ScannedFixtures.ScanAsync("move-cases");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        await cli.RunAsync("move", "plan", "--from", "Legacy", "--to", "Core", "--files", "src/Legacy/Clean/Money.cs", "src/Legacy/Orders/OrderMapper.cs", "src/Legacy/Json/Serializer.cs", "--out", "move-plan.json");
        var plan = repository.Directory.Read("move-plan.json");
        var stale = JsonNode.Parse(plan)!;
        stale["workspaceHash"] = "sha256:" + new string('0', 64);
        repository.Directory.Write("stale-plan.json", stale.ToJsonString());

        var refused = await cli.RunAsync("move", "apply", "--plan", "stale-plan.json", "--verify", "none", "--json");

        Assert.Equal(3, refused.ExitCode);
        Assert.Contains(JsonNode.Parse(refused.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0002");
        Assert.True(repository.Directory.Exists("src/Legacy/Clean/Money.cs"));

        File.AppendAllText(repository.Directory.Combine("src", "Legacy", "Clean", "Money.cs"), "// edited\n");
        var partial = await cli.RunAsync("move", "apply", "--plan", "move-plan.json", "--verify", "none", "--json");

        Assert.Equal(4, partial.ExitCode);
        var result = JsonNode.Parse(partial.Out)!["result"]!;
        Assert.Equal(["src/Legacy/Clean/Money.cs", "src/Legacy/Orders/OrderMapper.cs"], result["skipped"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(["src/Legacy/Json/Serializer.cs"], result["moved"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(2, JsonNode.Parse(partial.Out)!["diagnostics"]!.AsArray().Count(d => d!["code"]!.GetValue<string>() == "OFR2150"));
    }

    [Fact]
    public async Task Hollowing_out_500_files_verifies_once_at_the_end()
    {
        var fixture = await ScannedFixtures.ScanGeneratedAsync("hollow", root => GeneratedFixtures.Hollow(root));
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var plan = await cli.RunAsync("move", "plan", "--from", "src/Big/Big.csproj", "--to", "src/Big.Core/Big.Core.csproj", "--all", "--out", "move-plan.json", "--json");

        Assert.Equal(0, plan.ExitCode);
        var document = JsonNode.Parse(repository.Directory.Read("move-plan.json"))!;
        Assert.Equal(GeneratedFixtures.HollowFiles(), document["moves"]!.AsArray().Count);
        Assert.Empty(document["excluded"]!.AsArray());
        Assert.Equal(
            ["src/Big/Big.csproj addProjectReference src/Big.Core/Big.Core.csproj"],
            document["projectEdits"]!.AsArray().Select(e => $"{e!["project"]} {e["kind"]} {e["value"]}"));

        var run = await cli.RunAsync("move", "apply", "--plan", "move-plan.json", "--json");

        Assert.Equal(0, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        var verification = Assert.Single(result["verifications"]!.AsArray())!;
        Assert.Equal("passed", verification["status"]!.GetValue<string>());
        Assert.Equal(GeneratedFixtures.HollowFiles(), result["moved"]!.AsArray().Count);
        var staged = (await repository.GitAsync("diff", "--cached", "-M100%", "--name-status")).StandardOutput.Trim().Split('\n');
        Assert.Equal(GeneratedFixtures.HollowFiles(), staged.Length);
        Assert.All(staged, line => Assert.StartsWith("R100\tsrc/Big/", line, StringComparison.Ordinal));
    }
}
