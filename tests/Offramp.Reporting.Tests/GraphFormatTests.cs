using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offramp.Core.Json;
using Offramp.Fixtures;
using Offramp.Reporting.Graph;

namespace Offramp.Reporting.Tests;

/// <summary>docs/spec/commands/graph.md, acceptance: snapshots per format and fixture, and an offline HTML page.</summary>
public sealed partial class GraphFormatTests
{
    public static TheoryData<string, GraphFormat> Cases()
    {
        var data = new TheoryData<string, GraphFormat>();
        foreach (var fixture in new[] { "dual-target", "cycle", "netfx-only", "tests-in-prod" })
        {
            foreach (var format in new[] { GraphFormat.Json, GraphFormat.Dot, GraphFormat.Mermaid })
            {
                data.Add(fixture, format);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task Rendering_matches_the_snapshot(string fixture, GraphFormat format)
    {
        var graph = GraphView.Build(FixtureModels.Load(fixture), new GraphViewOptions());
        var extension = format switch { GraphFormat.Json => "json", GraphFormat.Dot => "dot", _ => "mmd" };

        return Verify(GraphRenderer.Render(graph, format, "Graph"), extension: extension).UseParameters(fixture, format);
    }

    [Fact]
    public void Json_validates_against_the_schema()
    {
        foreach (var fixture in new[] { "dual-target", "cycle", "netfx-only", "windows-only-build-steps" })
        {
            var graph = GraphView.Build(FixtureModels.Load(fixture), new GraphViewOptions { Cluster = GraphClusterMode.Directory, Highlight = GraphHighlightMode.Blockers });
            SchemaAssert.Valid("graph-document", GraphRenderer.Render(graph, GraphFormat.Json, "Graph"));
        }
    }

    [Fact]
    public void Clusters_appear_in_dot_and_mermaid()
    {
        var graph = GraphView.Build(FixtureModels.Load("dual-target"), new GraphViewOptions { Cluster = GraphClusterMode.Kind });

        Assert.Contains("subgraph cluster_0 {\n    label=\"console\";", DotWriter.Write(graph), StringComparison.Ordinal);
        Assert.Contains("subgraph c1[\"library\"]", MermaidWriter.Write(graph), StringComparison.Ordinal);
    }

    [Fact]
    public void Html_opens_offline_and_embeds_the_data()
    {
        var graph = GraphView.Build(FixtureModels.Load("cycle"), new GraphViewOptions());

        var html = HtmlGraphWriter.Write(graph, "Cycle </script> & friends");

        foreach (Match reference in ReferenceAttribute().Matches(html))
        {
            var target = reference.Groups["url"].Value;
            Assert.True(!target.Contains("http", StringComparison.OrdinalIgnoreCase) || target == "https://github.com/Andorbal/offramp",
                $"The page must not load {target}.");
        }

        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>Cycle &lt;/script&gt; &amp; friends</title>", html, StringComparison.Ordinal);
        var embedded = EmbeddedData().Match(html);
        Assert.True(embedded.Success);
        var roundTripped = JsonNode.Parse(embedded.Groups["json"].Value)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(OfframpJson.Serialize(graph, ReportingJsonContext.Default.GraphDocument)), roundTripped));
    }

    [Fact]
    public void Embedded_json_cannot_close_its_script_element()
    {
        Assert.Equal("{\"a\":\"<\\/script>\"}", HtmlGraphWriter.EmbeddableJson("{\"a\":\"</script>\"}\n"));
    }

    [GeneratedRegex("""(?:src|href)\s*=\s*["'](?<url>[^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceAttribute();

    [GeneratedRegex("""<script type="application/json" id="offramp-graph">(?<json>.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex EmbeddedData();
}
