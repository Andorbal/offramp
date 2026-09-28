using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Reporting.Graph;

namespace Offramp.Reporting.Tests;

/// <summary>
/// The HTML page's layout (ADR 0033), run by Node.js on the page's own script and data: every edge
/// starts at its source and ends at its target, runs only horizontally and vertically, never
/// crosses a box, and two runs agree. GraphLayoutCheck.js also proves it catches a box on a route.
/// </summary>
public sealed class GraphLayoutTests
{
    public static TheoryData<string> Graphs => ["dual-target", "cycle", "netfx-only", "tests-in-prod", "windows-only-build-steps", Monolith];

    private const string Monolith = "synthetic-monolith";

    [Theory]
    [MemberData(nameof(Graphs))]
    public async Task The_page_routes_every_edge_around_the_boxes(string name)
    {
        var node = await ProcessRunner.Instance.RunAsync(new ProcessSpec("node", ["--version"]), TestContext.Current.CancellationToken);
        Assert.SkipWhen(!node.Succeeded, "Node.js is not installed; the CI runners have it.");
        var graph = name == Monolith ? SyntheticMonolith() : GraphView.Build(FixtureModels.Load(name), new GraphViewOptions());
        using var scratch = new ScratchDirectory("graph-layout");
        var page = scratch.Write("graph.html", HtmlGraphWriter.Write(graph, name));

        var run = await ProcessRunner.Instance.RunAsync(
            new ProcessSpec("node", [Path.Combine(AppContext.BaseDirectory, "GraphLayoutCheck.js"), page]), TestContext.Current.CancellationToken);

        Assert.True(run.Succeeded, run.StandardOutput + run.StandardError);
        var report = JsonNode.Parse(run.StandardOutput)!;
        Assert.Empty(report["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal(graph.Edges.Count > 0, report["selfTestCaught"]!.GetValue<bool>());
    }

    /// <summary>
    /// Ninety projects in layers, as a large .NET Framework solution has them: shared libraries,
    /// domain, data, services, integrations, hosts, a test project for most, references that skip
    /// layers, two cycles, and HintPath references.
    /// </summary>
    internal static GraphDocument SyntheticMonolith()
    {
        var template = GraphView.Build(FixtureModels.Load("cycle"), new GraphViewOptions());
        var nodes = new List<GraphNode>();
        var edges = new SortedSet<(string From, string To, GraphEdgeKind Kind)>();
        var seed = 7u;
        int Next(int max) { seed = seed * 1664525 + 1013904223; return (int)(seed >> 8) % max; }

        string Add(string directory, string name, ProjectKind kind = ProjectKind.Library, FrameworkClass framework = FrameworkClass.Framework)
        {
            var id = $"{directory}/{name}/{name}.csproj";
            nodes.Add(template.Nodes[0] with { Id = id, Name = name, Kind = kind, FrameworkClass = framework, Directory = directory, Blockers = [], InCycle = false });
            return id;
        }

        void Edge(string from, string to, GraphEdgeKind kind = GraphEdgeKind.Project)
        {
            if (from != to)
            {
                edges.Add((from, to, kind));
            }
        }

        string[] areas = ["Billing", "Orders", "Customers", "Inventory", "Shipping", "Reporting", "Catalog", "Payments"];
        var core = new[] { "Common", "Logging", "Configuration", "Utilities", "Security", "Caching" }.Select(n => Add("src/Core", "Contoso." + n)).ToArray();
        var domain = areas.Select(a => Add("src/Domain", $"Contoso.{a}.Domain")).ToArray();
        var data = areas.Select(a => Add("src/Data", $"Contoso.{a}.Data")).ToArray();
        var services = areas.Append("Notifications").Append("Integration").Select(a => Add("src/Services", $"Contoso.{a}.Services")).ToArray();
        var integration = new[] { "Sap", "Salesforce", "Ftp", "Email", "Queue", "Legacy" }.Select(n => Add("src/Integration", "Contoso.Integration." + n)).ToArray();
        var hosts = new[]
        {
            ("Web", ProjectKind.Web), ("Admin.Web", ProjectKind.Web), ("Api", ProjectKind.Web), ("Partner.Api", ProjectKind.Web), ("Worker", ProjectKind.Service),
            ("Scheduler", ProjectKind.Service), ("Importer", ProjectKind.Console), ("Exporter", ProjectKind.Console), ("Desktop", ProjectKind.Winforms), ("Tools", ProjectKind.Console),
        }.Select(h => Add("src/Hosts", "Contoso." + h.Item1, h.Item2)).ToArray();
        var modern = new[] { ("Contracts", FrameworkClass.Standard), ("Abstractions", FrameworkClass.Standard), ("Api.Next", FrameworkClass.Modern), ("Shared.Next", FrameworkClass.Dual) }
            .Select(m => Add("src/Modern", "Contoso." + m.Item1, framework: m.Item2)).ToArray();
        var testing = Add("tests", "Contoso.Testing");

        foreach (var c in core.Skip(1)) { Edge(c, core[0]); }
        Edge(core[3], core[1]); Edge(core[4], core[2]); Edge(core[5], core[2]);
        for (var i = 0; i < areas.Length; i++)
        {
            Edge(domain[i], core[0]);
            if (i % 3 == 0) { Edge(domain[i], modern[0]); }
            if (i > 0 && Next(10) < 4) { Edge(domain[i], domain[Next(i)]); }
            Edge(data[i], domain[i]); Edge(data[i], core[0]); Edge(data[i], core[1]); Edge(data[i], core[2]);
            Edge(services[i], data[i]); Edge(services[i], domain[i]);
        }

        foreach (var s in services)
        {
            Edge(s, core[0]); Edge(s, core[1]);
            if (Next(2) == 0) { Edge(s, core[5]); }
            if (Next(2) == 0) { Edge(s, services[Next(services.Length)]); }
        }

        for (var i = 0; i < integration.Length; i++)
        {
            Edge(integration[i], core[0]); Edge(integration[i], core[2]); Edge(integration[i], services[Next(areas.Length)]);
            if (i % 2 == 1) { Edge(integration[i], modern[1]); }
        }

        for (var i = 0; i < hosts.Length; i++)
        {
            for (var k = 0; k < 4; k++) { Edge(hosts[i], services[Next(services.Length)]); }
            Edge(hosts[i], core[0]); Edge(hosts[i], core[1]);
            if (i < 4) { Edge(hosts[i], core[4]); }
            else { Edge(hosts[i], integration[Next(integration.Length)]); }
        }

        Edge(modern[2], modern[0]); Edge(modern[2], modern[1]); Edge(modern[3], modern[0]); Edge(modern[2], modern[3]);
        Edge(testing, core[0]);
        foreach (var target in domain.Concat(data).Concat(services).Concat(integration).Concat(hosts.Take(6)))
        {
            var test = Add("tests", target.Split('/')[^2] + ".Tests", ProjectKind.Test);
            Edge(test, target); Edge(test, testing);
            if (Next(2) == 0) { Edge(test, core[0]); }
        }

        Edge(services[1], services[9]); Edge(services[9], services[1]);
        Edge(integration[5], hosts[6], GraphEdgeKind.Assembly); Edge(hosts[6], integration[5]);
        Edge(data[3], data[4], GraphEdgeKind.Assembly);
        string[][] cycles = [[services[1], services[9]], [hosts[6], integration[5]]];
        var inCycle = cycles.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);

        return template with
        {
            Nodes = [.. nodes.Select(n => n with { InCycle = inCycle.Contains(n.Id) }).OrderBy(n => n.Id, StringComparer.Ordinal)],
            Edges = [.. edges.Select(e => new GraphEdge(e.From, e.To, e.Kind))],
            Cycles = cycles,
            Highlight = new GraphHighlight(GraphHighlightMode.Cycles, [.. inCycle.Order(StringComparer.Ordinal)]),
        };
    }
}
