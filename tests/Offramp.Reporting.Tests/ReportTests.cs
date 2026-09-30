using System.Text.RegularExpressions;
using Offramp.Analysis.DeadCode;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Reporting.Graph;
using Offramp.Reporting.Report;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;
using static Offramp.Reporting.Tests.GraphViewTests;

namespace Offramp.Reporting.Tests;

/// <summary>docs/spec/commands/workspace.md#report: the data, the charts, and the three renderings.</summary>
public sealed partial class ReportTests
{
    // App (console, framework) → Core (framework) → Contracts (standard); Svc (service, framework) → Contracts;
    // Web (web, dual) → Contracts; Core.Tests → Core; legacy/Legacy ↔ legacy/Old (assembly edge) is a cycle.
    private static readonly WorkspaceModel Model = ModelOf(
        Project("src/App/App.csproj", ProjectKind.Console, FrameworkClass.Framework, "src/Core/Core.csproj") with { Loc = 1200 },
        Project("src/Core/Core.csproj", ProjectKind.Library, FrameworkClass.Framework, "src/Contracts/Contracts.csproj") with { Loc = 5400 },
        Project("src/Contracts/Contracts.csproj", ProjectKind.Library, FrameworkClass.Standard) with { Loc = 800 },
        Project("src/Svc/Svc.csproj", ProjectKind.Service, FrameworkClass.Framework, "src/Contracts/Contracts.csproj") with { Loc = 2100 },
        Project("src/Web/Web.csproj", ProjectKind.Web, FrameworkClass.Dual, "src/Contracts/Contracts.csproj") with { Loc = 3300 },
        Project("tests/Core.Tests/Core.Tests.csproj", ProjectKind.Test, FrameworkClass.Framework, "src/Core/Core.csproj") with { Loc = 900 },
        Project("legacy/Legacy/Legacy.csproj", ProjectKind.Library, FrameworkClass.Framework, "legacy/Old/Old.csproj") with { Loc = 4000 },
        Project("legacy/Old/Old.csproj", ProjectKind.Library, FrameworkClass.Framework) with
        {
            Loc = 2500,
            AssemblyReferences = [new AssemblyReferenceInfo { Name = "Legacy", HintPath = "legacy/Legacy/bin/Legacy.dll", Kind = AssemblyReferenceKind.File }],
        });

    /// <summary>Three earlier scans: Web and Contracts were framework-only, and Core was bigger.</summary>
    private static readonly LedgerSnapshot[] History =
    [
        Earlier("2026-06-02T09:00:00Z", p => p.Id is "src/Web/Web.csproj" or "src/Contracts/Contracts.csproj" ? p with { FrameworkClass = FrameworkClass.Framework } : p),
        Earlier("2026-07-15T09:00:00Z", p => p.Id == "src/Web/Web.csproj" ? p with { FrameworkClass = FrameworkClass.Framework } : p),
        Earlier("2026-08-30T09:00:00Z", p => p.Id == "src/Core/Core.csproj" ? p with { Loc = 6100 } : p),
    ];

    [Fact]
    public void Series_ends_with_the_model_and_respects_since()
    {
        var all = ReportBuilder.Build(Model, History, "Monolith", since: null);
        Assert.Equal(["2026-06-02T09:00:00Z", "2026-07-15T09:00:00Z", "2026-08-30T09:00:00Z", Model.CreatedAt], all.Series.Select(p => p.CreatedAt));
        Assert.Equal(Model.CreatedAt, all.AsOf);

        var recent = ReportBuilder.Build(Model, History, "Monolith", since: "2026-07-15");
        Assert.Equal(["2026-07-15T09:00:00Z", "2026-08-30T09:00:00Z", Model.CreatedAt], recent.Series.Select(p => p.CreatedAt));

        var none = ReportBuilder.Build(Model, [], "Monolith", since: null);
        Assert.Equal([Model.CreatedAt], none.Series.Select(p => p.CreatedAt));
        Assert.Equal(0, none.Headline.FrameworkLocChange);
    }

    /// <summary>
    /// NHibernate P2: after scans of a solution filter and then of the solution, the trend compared the two ("down 232
    /// since"); only snapshots of the model's solution make the series.
    /// </summary>
    [Fact]
    public void Snapshots_of_another_solution_are_left_out_of_the_series()
    {
        var filter = Ledger.Snapshot(Model with { CreatedAt = "2026-09-01T09:00:00Z", Solution = "Everything.slnf", Projects = [.. Model.Projects.Take(2)] });
        var unnamed = Ledger.Snapshot(Model with { CreatedAt = "2026-09-02T09:00:00Z", Solution = null });

        var report = ReportBuilder.Build(Model, [.. History, filter, unnamed], "Monolith", since: null);

        Assert.Equal(["2026-06-02T09:00:00Z", "2026-07-15T09:00:00Z", "2026-08-30T09:00:00Z", Model.CreatedAt], report.Series.Select(p => p.CreatedAt));
        var (kept, others) = ReportBuilder.OfModelSolution(Model, [.. History, filter, unnamed]);
        Assert.Equal(3, kept.Count);
        Assert.Equal([null, "Everything.slnf"], others);
    }

    [Fact]
    public void Snapshots_newer_than_the_model_or_repeating_it_are_left_out()
    {
        var future = Earlier("2026-12-01T00:00:00Z", p => p);
        var same = Ledger.Snapshot(Model with { Projects = [.. Model.Projects.Select(p => p with { Loc = 1 })] });

        var report = ReportBuilder.Build(Model, [future, same, .. History], "Monolith", since: null);

        Assert.Equal(4, report.Series.Count);
        Assert.Equal(Model.Projects.Sum(p => p.Loc), report.Series[^1].Loc);
    }

    [Fact]
    public void Headline_counts_portable_lines_progress_and_applications()
    {
        var report = ReportBuilder.Build(Model, History, "Monolith", since: null);
        var h = report.Headline;

        Assert.Equal(20_200, h.Loc);
        Assert.Equal(16_100, h.FrameworkLoc);
        Assert.Equal(6, h.FrameworkProjects);
        Assert.Equal(20.3, h.PortablePercent);
        Assert.Equal(16_100 - (16_100 + 3300 + 800), h.FrameworkLocChange);
        Assert.Equal(3, h.Applications);
        Assert.Equal(1, h.ApplicationsDone);
        Assert.Equal(2, h.Ready);
    }

    [Fact]
    public void Applications_show_what_is_left_in_their_closure()
    {
        var byName = ReportBuilder.Build(Model, History, "Monolith", null).Applications.ToDictionary(a => a.Name);

        Assert.Equal(["App", "Svc", "Web"], byName.Keys.Order(StringComparer.Ordinal));
        var app = byName["App"];
        Assert.Equal((ProjectReadiness.Blocked, 3, 2, 6600), (app.Status, app.Closure, app.Remaining, app.RemainingLoc));
        Assert.Equal(["src/Core/Core.csproj"], app.Next);
        var svc = byName["Svc"];
        Assert.Equal((ProjectReadiness.Ready, 1), (svc.Status, svc.Remaining));
        Assert.Equal(["src/Svc/Svc.csproj"], svc.Next);
        var web = byName["Web"];
        Assert.Equal((ProjectReadiness.Done, 0, 0), (web.Status, web.Remaining, web.RemainingLoc));
        Assert.Empty(web.Next);
    }

    /// <summary>
    /// SmartStoreNET P1 #5: plugins that build into the site are part of the site's application, not 12 more
    /// applications, and what they need is left for the site.
    /// </summary>
    [Fact]
    public void Hosted_web_projects_are_in_their_host_closure_and_not_applications()
    {
        var host = new ProjectHost { Project = "src/Web/Web.csproj", Evidence = ["builds into src/Web/Plugins/Tax, inside src/Web"] };
        var model = ModelOf(
            [.. Model.Projects,
            Project("src/Plugins/Tax/Tax.csproj", ProjectKind.Web, FrameworkClass.Framework, "src/Core/Core.csproj") with { Loc = 700, HostedBy = host },
            Project("src/Plugins/Geo/Geo.csproj", ProjectKind.Web, FrameworkClass.Framework) with { Loc = 300, HostedBy = host }]);

        var report = ReportBuilder.Build(model, [], "Monolith", null);

        Assert.Equal(3, report.Headline.Applications);
        var web = report.Applications.Single(a => a.Name == "Web");
        Assert.Equal((ProjectReadiness.Blocked, 5, 3, 6400), (web.Status, web.Closure, web.Remaining, web.RemainingLoc));
        Assert.Equal(["src/Plugins/Geo/Geo.csproj", "src/Plugins/Tax/Tax.csproj"], web.Hosted);
        Assert.Equal(["src/Core/Core.csproj", "src/Plugins/Geo/Geo.csproj"], web.Next);
        Assert.Contains("`src/Web/Web.csproj` (hosts 2)", ReportRenderer.Render(report, ReportFormat.Markdown, null, null), StringComparison.Ordinal);
    }

    /// <summary>
    /// NHibernate P1 #7: a repository of libraries said "applications 0" and nothing about the libraries. The
    /// libraries other code uses (ADR 0041) are counted, listed with what is left in their closure, and every
    /// rendering talks about them when there is no application.
    /// </summary>
    [Fact]
    public void A_repository_without_applications_reports_the_libraries_other_code_uses()
    {
        var model = ModelOf(
            Project("src/NHibernate/NHibernate.csproj", ProjectKind.Library, FrameworkClass.Framework) with { Loc = 264_000 },
            Project("src/NHibernate.DomainModel/NHibernate.DomainModel.csproj", ProjectKind.Library, FrameworkClass.Framework, "src/NHibernate/NHibernate.csproj") with { Loc = 9_000 },
            Project("src/NHibernate.Test/NHibernate.Test.csproj", ProjectKind.Test, FrameworkClass.Framework, "src/NHibernate/NHibernate.csproj", "src/NHibernate.DomainModel/NHibernate.DomainModel.csproj") with { Loc = 201_000 });
        using var root = new ScratchDirectory("report");
        root.Write("src/NHibernate/NHibernate.nuspec", "<package><files><file src=\"bin/NHibernate.dll\" target=\"lib/net40\" /></files></package>\n");

        var report = ReportBuilder.Build(model, [], "NHibernate", null, ShippedProjects.Read(root.Path, model, []));

        Assert.Equal((0, 0, 2, 0), (report.Headline.Applications, report.Headline.ApplicationsDone, report.Headline.Libraries, report.Headline.LibrariesDone));
        Assert.Equal(["src/NHibernate.DomainModel/NHibernate.DomainModel.csproj", "src/NHibernate/NHibernate.csproj"], report.Libraries.Select(l => l.Project));
        var nhibernate = report.Libraries[1];
        Assert.Equal((ProjectReadiness.Ready, 1, 1, 264_000, "packed by src/NHibernate/NHibernate.nuspec"), (nhibernate.Status, nhibernate.Closure, nhibernate.Remaining, nhibernate.RemainingLoc, nhibernate.Shipped));
        var domain = report.Libraries[0];
        Assert.Equal((ProjectReadiness.Blocked, "no application in the solution uses it"), (domain.Status, domain.Shipped));
        Assert.Equal(["src/NHibernate/NHibernate.csproj"], domain.Next);
        Assert.EndsWith("and 0 of 2 libraries are done.", ReportText.Summary(report), StringComparison.Ordinal);
        var markdown = ReportRenderer.Render(report, ReportFormat.Markdown);
        Assert.Contains("- **Libraries done:** 0 of 2 of the libraries other code uses.", markdown, StringComparison.Ordinal);
        Assert.Contains("## Libraries", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Applications", markdown, StringComparison.Ordinal);
        var html = ReportRenderer.Render(report, ReportFormat.Html);
        Assert.Contains("libraries with nothing left to port", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Libraries</h2>", html, StringComparison.Ordinal);
        SchemaAssert.Valid("report-data", ReportRenderer.Render(report, ReportFormat.Json));

        // With applications, the renderings stay about them: the Monolith's unused libraries are data only.
        var monolith = ReportBuilder.Build(Model, History, "Monolith", null, ShippedProjects.Read(root.Path, Model, []));
        Assert.Equal(["legacy/Legacy/Legacy.csproj", "legacy/Old/Old.csproj"], monolith.Libraries.Select(l => l.Project));
        Assert.EndsWith("and 1 of 3 applications are done.", ReportText.Summary(monolith), StringComparison.Ordinal);
        Assert.DoesNotContain("## Libraries", ReportRenderer.Render(monolith, ReportFormat.Markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void Frontier_is_ready_projects_most_depended_on_first_and_cycles_never_are()
    {
        var frontier = ReportBuilder.Build(Model, History, "Monolith", null).Frontier;

        Assert.Equal(["src/Core/Core.csproj", "src/Svc/Svc.csproj"], frontier.Select(f => f.Project));
        Assert.Equal(2, frontier[0].Dependents);
    }

    [Fact]
    public void Areas_group_projects_by_the_directory_holding_their_folders()
    {
        var areas = ReportBuilder.Build(Model, History, "Monolith", null).Areas;

        Assert.Equal(["legacy", "src", "tests"], areas.Select(a => a.Area));
        var src = areas[1];
        Assert.Equal((5, 12_800), (src.Projects, src.Loc));
        Assert.Equal(new ClassTotals(3, 8700), src.ByFrameworkClass["framework"]);
        Assert.Equal(new ClassTotals(0, 0), src.ByFrameworkClass["modern"]);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(7, 8, 2)]
    [InlineData(20_200, 30_000, 10_000)]
    [InlineData(1_000_000, 1_000_000, 500_000)]
    [InlineData(38, 40, 10)]
    public void Axis_scale_is_round_and_covers_the_maximum(int value, double max, double step)
    {
        Assert.Equal((max, step), Charts.Scale(value));
    }

    [Fact]
    public void Charts_are_deterministic_and_script_free()
    {
        var report = ReportBuilder.Build(Model, History, "Monolith", null);

        var burnDown = Charts.BurnDown(report.Series);
        Assert.Equal(burnDown, Charts.BurnDown(report.Series));
        Assert.Equal(4, Regex.Count(burnDown, "<path class=\"band\""));
        Assert.Equal(report.Series.Count, Regex.Count(burnDown, "<circle "));
        Assert.Contains("2026-06-02", burnDown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", burnDown, StringComparison.Ordinal);

        var single = Charts.BurnDown(report.Series.TakeLast(1).ToList());
        Assert.DoesNotContain("<path", single, StringComparison.Ordinal);
        Assert.Contains("<rect", single, StringComparison.Ordinal);

        var areas = Charts.ByArea(report.Areas);
        Assert.Contains("<title>src: framework, 3 projects, 8,700 lines</title>", areas, StringComparison.Ordinal);
    }

    [Fact]
    public Task Html_matches_the_snapshot() =>
        Verify(ReportRenderer.Render(ReportBuilder.Build(Model, History, "Monolith migration", "2026-06-01"), ReportFormat.Html), extension: "html");

    [Fact]
    public Task Markdown_matches_the_snapshot() =>
        Verify(ReportRenderer.Render(ReportBuilder.Build(Model, History, "Monolith | migration", null), ReportFormat.Markdown), extension: "md");

    [Fact]
    public Task Json_matches_the_snapshot_and_the_schema()
    {
        var json = ReportRenderer.Render(ReportBuilder.Build(Model, History, "Monolith", null), ReportFormat.Json);
        SchemaAssert.Valid("report-data", json);
        return Verify(json, extension: "json");
    }

    [Fact]
    public void Fixture_reports_validate_against_the_schema()
    {
        foreach (var fixture in new[] { "dual-target", "cycle", "netfx-only", "versions", "windows-only-build-steps" })
        {
            var model = FixtureModels.Load(fixture) with { CreatedAt = "2026-09-25T20:11:04Z" };
            SchemaAssert.Valid("report-data", ReportRenderer.Render(ReportBuilder.Build(model, [], fixture, null), ReportFormat.Json));
        }
    }

    [Fact]
    public void Html_opens_offline_without_scripts()
    {
        var html = ReportRenderer.Render(ReportBuilder.Build(Model, History, "Monolith </style> & co", null), ReportFormat.Html);

        AssertOffline(html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>Monolith &lt;/style&gt; &amp; co</title>", html, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme: dark", html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_is_embedded_in_a_sandboxed_frame_with_its_data_intact()
    {
        var graph = HtmlGraphWriter.Write(GraphView.Build(Model, new GraphViewOptions()), "Graph </script>");

        var html = HtmlReportWriter.Write(ReportBuilder.Build(Model, History, "Monolith", null), graph);

        var frame = SourceDocument().Match(html);
        Assert.True(frame.Success);
        Assert.Contains("sandbox=\"allow-scripts allow-downloads\"", html, StringComparison.Ordinal);
        Assert.Equal(graph, System.Net.WebUtility.HtmlDecode(frame.Groups["doc"].Value));
        var page = html.Replace(frame.Value, "", StringComparison.Ordinal);
        AssertOffline(page);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertOffline(string html)
    {
        foreach (Match reference in ReferenceAttribute().Matches(html))
        {
            Assert.DoesNotContain("http", reference.Groups["url"].Value, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.Ordinal);
    }

    private static LedgerSnapshot Earlier(string createdAt, Func<ProjectInfo, ProjectInfo> change) =>
        Ledger.Snapshot(Model with { CreatedAt = createdAt, Projects = [.. Model.Projects.Select(change)] });

    [GeneratedRegex("""(?:src|href)="(?<url>[^"]*)""")]
    private static partial Regex ReferenceAttribute();

    [GeneratedRegex("""srcdoc="(?<doc>[^"]*)""")]
    private static partial Regex SourceDocument();
}
