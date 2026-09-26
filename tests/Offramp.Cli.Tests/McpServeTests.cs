using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Offramp.Cli.Commands;
using Offramp.Core.Caching;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

/// <summary>
/// ROADMAP M13 acceptance: an MCP client starts <c>offramp mcp serve</c> as a process, lists the
/// tools, runs <c>offramp_deps_audit</c> on the versions model with its recorded feed, and
/// receives progress. Also: every call is a dry run without <c>--allow-apply</c>, paths outside
/// <c>--root</c> are refused (OFR9101), and the resources read the state directory.
/// </summary>
public sealed class McpServeTests
{
    [Fact]
    [ProducesDiagnostic("OFR9101")]
    public async Task A_client_lists_tools_runs_deps_audit_with_progress_and_calls_are_dry_runs()
    {
        using var repo = new ScratchDirectory("mcp");
        VersionsFeed.WriteFolderFeed(repo.Combine("recorded-feed"));
        repo.Write("offramp.yml", "version: 1\ndeps:\n  feeds: [ recorded-feed ]\n");
        var model = FixtureModels.Load("versions");
        WorkspaceStore.Save(repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(repo.Path, repo.Combine(".offramp"), model.Solution),
        });
        await ProcessRunner.Instance.RunAsync(new ProcessSpec("git", ["init", "-q"]) { WorkingDirectory = repo.Path }, TestContext.Current.CancellationToken);
        var config = ContentHash.Sha256File(repo.Combine("offramp.yml"));

        var stderr = new List<string>();
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "offramp",
            Command = "dotnet",
            Arguments = [typeof(OfframpCli).Assembly.Location, "mcp", "serve", "--root", repo.Path],
            WorkingDirectory = repo.Path,
            StandardErrorLines = line => { lock (stderr) { stderr.Add(line); } },
        }), cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).ToList();
        Assert.Contains("offramp_help", names);
        Assert.Contains("offramp_deps_audit", names);
        Assert.Contains("offramp_move_plan", names);
        Assert.Contains("offramp_deps_resolve_dlls", names);
        Assert.Contains("offramp_guide", names);
        Assert.DoesNotContain(names, n => n.StartsWith("offramp_mcp", StringComparison.Ordinal));
        var audit = tools.Single(t => t.Name == "offramp_deps_audit").JsonSchema;
        Assert.True(audit.GetProperty("properties").TryGetProperty("package", out _));
        Assert.Equal(["error", "info", "never", "warning"], audit.GetProperty("properties").GetProperty("fail-on").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
        var movePlan = tools.Single(t => t.Name == "offramp_move_plan").JsonSchema;
        Assert.Contains("from", movePlan.GetProperty("required").EnumerateArray().Select(e => e.GetString()));

        var help = await client.CallToolAsync("offramp_help", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("offramp_scan", Text(help), StringComparison.Ordinal);

        await using var listener = new ProgressListener(client);
        var result = await client.CallToolAsync("offramp_deps_audit", new Dictionary<string, object?>(), listener, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError != true, Text(result) + string.Join('\n', stderr));
        var envelope = Text(result);
        SchemaAssert.ValidEnvelope(envelope, "deps-audit");
        Assert.Equal("deps audit", JsonNode.Parse(envelope)!["offramp"]!["command"]!.GetValue<string>());
        var progress = await listener.WaitAsync(p => p.Count > 0 && p.Max(v => v.Progress) == p.Count, TestContext.Current.CancellationToken);
        Assert.NotEmpty(progress);
        // Numbered 1..n without gaps or repeats (the client handles notifications concurrently, so arrival order is not checked).
        Assert.Equal(Enumerable.Range(1, progress.Count).Select(i => (float)i), progress.Select(p => p.Progress).Order());

        var init = await client.CallToolAsync("offramp_init", new Dictionary<string, object?> { ["defaults"] = true, ["force"] = true, ["apply"] = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(init.Content.OfType<TextContentBlock>(), c => c.Text.StartsWith("Dry run: this server was started without --allow-apply", StringComparison.Ordinal));
        Assert.Equal(config, ContentHash.Sha256File(repo.Combine("offramp.yml")));

        var outside = await client.CallToolAsync("offramp_deps_audit", new Dictionary<string, object?> { ["config"] = "../elsewhere/offramp.yml" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(outside.IsError);
        Assert.Equal("OFR9101", JsonNode.Parse(Text(outside))!["code"]!.GetValue<string>());

        var workspace = await client.ReadResourceAsync("offramp://workspace", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("\"projects\"", workspace.Contents.OfType<TextResourceContents>().Single().Text, StringComparison.Ordinal);
        var diagnostic = await client.ReadResourceAsync("offramp://diagnostics/ofr9101", cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("# OFR9101", diagnostic.Contents.OfType<TextResourceContents>().Single().Text, StringComparison.Ordinal);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.ReadResourceAsync("offramp://plan/../workspace", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("src/App/App.csproj", true)]
    [InlineData("src/**/*.cs", true)]
    [InlineData("./a/../b.json", true)]
    [InlineData("http://legacy.internal/", true)]
    [InlineData("../other/App.csproj", false)]
    [InlineData("a/../../escape.json", false)]
    public void Paths_are_confined_to_the_root(string value, bool inside)
    {
        var root = Path.Combine(Path.GetTempPath(), "offramp-root");
        Assert.Equal(inside, McpToolCatalog.Inside(root, value));
        Assert.False(McpToolCatalog.Inside(root, Path.GetFullPath(Path.Combine(root, "..", "sibling"))));
        Assert.True(McpToolCatalog.Inside(root, Path.Combine(root, "inner", "x.json")));
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().First().Text;
}
