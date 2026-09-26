using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Fixtures;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#easily-movable and #new-code, over the ide-counterpart fixture.</summary>
public sealed class FileReportTests
{
    [Fact]
    [ProducesDiagnostic("OFR2101")]
    [ProducesDiagnostic("OFR2103")]
    [ProducesDiagnostic("OFR6003")]
    public async Task Each_file_moves_as_it_is_or_says_why()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var engine = await Engines.CreateAsync(fixture);

        var outcomes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[]
        {
            "src/Foo/Pricing/PriceCalculator.cs", "src/Foo/Formatting/Names.cs", "src/Foo/Orders/OrderService.cs",
            "src/Foo/Web/CookieReader.cs", "src/Foo/Json/Payload.cs", "src/Foo/Text/Casing.cs",
        })
        {
            var report = await engine.ReportAsync(file, CancellationToken.None);
            Assert.True(report.Applies, file);
            outcomes[file] = string.Join(" | ", report.Moves.Select(m => $"{IdeCheck.ProjectName(m.To)}: {(m.Movable ? "movable" : m.Code)}"));
        }

        Assert.Equal(
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/Foo/Formatting/Names.cs"] = "ModernF: movable | Shared: movable",
                ["src/Foo/Json/Payload.cs"] = "ModernF: OFR6003 | Shared: OFR6003",
                ["src/Foo/Orders/OrderService.cs"] = "ModernF: OFR2101 | Shared: OFR2101",
                ["src/Foo/Pricing/PriceCalculator.cs"] = "ModernF: movable | Shared: movable",
                ["src/Foo/Text/Casing.cs"] = "ModernF: OFR6003 | Shared: movable",
                ["src/Foo/Web/CookieReader.cs"] = "ModernF: OFR2103 | Shared: OFR2103",
            },
            outcomes);
    }

    [Fact]
    public async Task Reasons_name_what_is_missing_and_where_the_file_would_go()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var engine = await Engines.CreateAsync(fixture);

        var price = await engine.ReportAsync("src/Foo/Pricing/PriceCalculator.cs", CancellationToken.None);
        var order = await engine.ReportAsync("src/Foo/Orders/OrderService.cs", CancellationToken.None);
        var cookie = await engine.ReportAsync("src/Foo/Web/CookieReader.cs", CancellationToken.None);
        var payload = await engine.ReportAsync("src/Foo/Json/Payload.cs", CancellationToken.None);
        var casing = await engine.ReportAsync("src/Foo/Text/Casing.cs", CancellationToken.None);
        var names = await engine.ReportAsync("src/Foo/Formatting/Names.cs", CancellationToken.None);

        var toModernF = price.Moves.Single(m => m.To == Engines.ModernF);
        Assert.Equal("src/ModernF/Pricing/PriceCalculator.cs", toModernF.Destination);
        Assert.True(toModernF.Referenced);
        Assert.Equal(["src/Foo/Pricing/PriceCalculator.cs"], order.Moves[0].Details);
        Assert.Contains("'HttpContext'", Assert.Single(cookie.Moves[0].Details), StringComparison.Ordinal);
        Assert.Contains("Newtonsoft.Json", payload.Moves[0].Message, StringComparison.Ordinal);
        Assert.Equal("src/ModernF/Text/Casing.cs already exists.", casing.Moves[0].Message);
        Assert.Equal(["Foo.Formatting.NameStyle", "Foo.Formatting.Names"], names.Types.Select(t => t.Name));
        Assert.Equal("Foo.Formatting.Names", names.LensType()!.Name);
        Assert.Null(cookie.LensType());
    }

    [Fact]
    public async Task Files_outside_net_framework_projects_do_not_apply()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var engine = await Engines.CreateAsync(fixture);

        var modern = await engine.ReportAsync("src/ModernF/Text/Casing.cs", CancellationToken.None);
        var nowhere = await engine.ReportAsync("README.md", CancellationToken.None);

        Assert.Equal(Engines.ModernF, modern.Project);
        Assert.False(modern.Applies);
        Assert.Null(nowhere.Project);
        Assert.False(nowhere.Applies);
    }

    [Fact]
    [ProducesDiagnostic("OFR6001")]
    [ProducesDiagnostic("OFR6005")]
    [ProducesDiagnostic("OFR3001")]
    public async Task New_code_gets_findings_on_new_lines_and_new_types_that_could_move()
    {
        var scanned = await ScannedFixtures.ScanAsync(Engines.Fixture);
        using var fixture = scanned.Repository;
        fixture.Directory.Write("src/Foo/Pricing/TaxRule.cs", """
            namespace Foo.Pricing
            {
                public sealed class TaxRule
                {
                    public decimal Apply(decimal amount, decimal rate) => amount * (1 + rate);
                }
            }

            """);
        fixture.Directory.Write("src/Foo/Web/CookieReader.cs", """
            using System.Web;

            namespace Foo.Web
            {
                /// <summary>Uses System.Web: does not compile in ModernF (OFR2103).</summary>
                public static class CookieReader
                {
                    public static string Read(string name) => HttpContext.Current?.Request.Cookies[name]?.Value;

                    public static string Path() => HttpContext.Current?.Request.Path;
                }
            }

            """);
        fixture.Directory.Write("src/Legacy/Farewell.cs", """
            namespace Legacy
            {
                public static class Farewell
                {
                    public static string Bye(string name) => "Bye, " + name;
                }
            }

            """);
        using var engine = await Engines.CreateAsync(scanned);
        var diagnostics = new DiagnosticBag();

        var result = await IdeCheck.RunAsync(engine, null, diagnostics, CancellationToken.None);

        Assert.Equal(["src/Foo/Pricing/TaxRule.cs", "src/Foo/Web/CookieReader.cs", "src/Legacy/Farewell.cs"], result.Files.Select(f => f.File));
        var cookie = result.Files[1];
        Assert.Equal([[9, 10]], cookie.NewLines);
        Assert.Equal(10, Assert.Single(cookie.Findings).Line);
        Assert.Equal(
            ["OFR3001 src/Foo/Web/CookieReader.cs:10", "OFR6001 src/Foo/Pricing/TaxRule.cs:3", "OFR6005 src/Legacy/Legacy.csproj:"],
            diagnostics.ToSortedList().Where(d => d.Code != "OFR6007").Select(d => $"{d.Code} {d.File ?? d.Project}:{d.Line}").Order(StringComparer.Ordinal));
        var json = OfframpJson.Serialize(result, IdeJsonContext.Default.IdeCheckResult).Replace(result.Base.Commit!, "{Commit}", StringComparison.Ordinal);
        await Verify(json, extension: "json");
    }

    [Fact]
    public async Task Code_left_unchanged_is_not_new_and_scope_all_makes_it_new()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var lines = await Engines.CreateAsync(fixture);
        using var all = await Engines.CreateAsync(fixture, new IdeSettings { NewCode = new IdeNewCodeSettings { Scope = NewCode.All } });

        var unchanged = await lines.ReportAsync("src/Foo/Web/CookieReader.cs", CancellationToken.None);
        var everything = await all.ReportAsync("src/Foo/Web/CookieReader.cs", CancellationToken.None);

        Assert.Empty(unchanged.NewLines);
        Assert.Empty(unchanged.Findings);
        Assert.Equal([[1, 10]], everything.NewLines);
        Assert.Equal("OFR3001", Assert.Single(everything.Findings).Code);
    }

    [Fact]
    public async Task The_editor_text_wins_over_the_disk()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var engine = await Engines.CreateAsync(fixture);
        const string file = "src/Foo/Pricing/PriceCalculator.cs";
        var text = fixture.Repository.Directory.Read(file);

        engine.Workspace.SetOpenDocument(file, text.Replace("Math.Max(0m, prices.Sum() - discount)", "System.Web.HttpContext.Current == null ? 0m : prices.Sum()", StringComparison.Ordinal));
        var edited = await engine.ReportAsync(file, CancellationToken.None);
        engine.Workspace.CloseDocument(file);
        var closed = await engine.ReportAsync(file, CancellationToken.None);

        Assert.Equal("OFR3001", Assert.Single(edited.Findings).Code);
        Assert.All(edited.Moves, m => Assert.Equal("OFR2103", m.Code));
        Assert.Empty(closed.Findings);
        Assert.All(closed.Moves, m => Assert.True(m.Movable));
    }
}
