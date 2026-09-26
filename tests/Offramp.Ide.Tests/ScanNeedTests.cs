using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#projects-that-need-a-scan: the editor says when a project needs a scan, and why.</summary>
public sealed class ScanNeedTests
{
    [Fact]
    [ProducesDiagnostic("OFR6009")]
    public async Task A_project_added_or_changed_after_the_scan_needs_a_scan()
    {
        var fixture = await ScannedFixtures.ScanAsync(Engines.Fixture);
        using var repository = fixture.Repository;
        repository.Directory.Write("src/Billing/Billing.csproj", repository.Directory.Read("src/Legacy/Legacy.csproj").Replace("Legacy", "Billing", StringComparison.Ordinal));
        repository.Directory.Write("src/Billing/Invoice.cs", "namespace Billing\n{\n    public sealed class Invoice\n    {\n    }\n}\n");
        repository.Directory.Write("src/Foo/Foo.csproj", repository.Directory.Read("src/Foo/Foo.csproj").Replace("<LangVersion>latest</LangVersion>", "<LangVersion>latest</LangVersion>\n    <Deterministic>true</Deterministic>", StringComparison.Ordinal));
        using var engine = await Engines.CreateAsync(fixture);
        var diagnostics = new DiagnosticBag();

        var invoice = await engine.ReportAsync("src/Billing/Invoice.cs", CancellationToken.None);
        var price = await engine.ReportAsync("src/Foo/Pricing/PriceCalculator.cs", CancellationToken.None);
        var greeter = await engine.ReportAsync("src/Legacy/Greeter.cs", CancellationToken.None);
        await IdeCheck.RunAsync(engine, ["src/Billing/Invoice.cs", "src/Foo/Pricing/PriceCalculator.cs", "src/Legacy/Greeter.cs"], diagnostics, CancellationToken.None);

        Assert.Null(invoice.Project);
        Assert.Equal((IdeScanNeed.NewProject, "src/Billing/Billing.csproj"), (invoice.Scan!.Reason, invoice.Scan.Project));
        Assert.Equal((IdeScanNeed.ProjectChanged, Engines.Foo), (price.Scan!.Reason, price.Scan.Project));
        Assert.StartsWith("src/Foo/Foo.csproj changed after the last scan", price.Scan.Message, StringComparison.Ordinal);
        Assert.True(price.Applies);
        Assert.NotEmpty(price.Moves);
        Assert.Null(greeter.Scan);
        Assert.Equal(
            [("OFR6006", "src/Billing/Billing.csproj", "new-project"), ("OFR6009", "src/Foo/Foo.csproj", "project-changed")],
            diagnostics.ToSortedList().Where(d => d.Code is "OFR6006" or "OFR6009").Select(d => (d.Code, d.Project!, d.Data["reason"]!.GetValue<string>())));
    }

    [Fact]
    public async Task A_directory_props_file_above_projects_makes_each_of_them_need_a_scan()
    {
        var fixture = await ScannedFixtures.ScanAsync(Engines.Fixture);
        using var repository = fixture.Repository;
        repository.Directory.Write("Directory.Build.props", repository.Directory.Read("Directory.Build.props").Replace("</PropertyGroup>", "  <Features>strict</Features>\n  </PropertyGroup>", StringComparison.Ordinal));
        using var engine = await Engines.CreateAsync(fixture);

        var price = await engine.ReportAsync("src/Foo/Pricing/PriceCalculator.cs", CancellationToken.None);
        var greeter = await engine.ReportAsync("src/Legacy/Greeter.cs", CancellationToken.None);

        Assert.Equal(("Directory.Build.props", IdeScanNeed.ProjectChanged), (price.Scan!.Message.Split(' ')[0], price.Scan.Reason));
        Assert.Equal(Engines.Legacy, greeter.Scan!.Project);
    }

    [Fact]
    public async Task A_project_the_scan_could_not_compile_needs_a_scan_and_nothing_else_is_reported()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var engine = await Engines.CreateAsync(fixture, model: m => m with
        {
            Projects = [.. m.Projects.Select(p => p.Id == Engines.Foo ? p with { CompilerCalls = new(StringComparer.Ordinal) } : p)],
        });

        var price = await engine.ReportAsync("src/Foo/Pricing/PriceCalculator.cs", CancellationToken.None);
        var modern = await engine.ReportAsync("src/ModernF/Text/Casing.cs", CancellationToken.None);

        Assert.Equal((IdeScanNeed.NoCompilation, Engines.Foo), (price.Scan!.Reason, price.Scan.Project));
        Assert.False(price.Applies);
        Assert.Null(modern.Scan);
    }

    [Fact]
    public void The_scan_need_is_part_of_the_file_report_json()
    {
        var report = new IdeFileReport
        {
            File = "src/Billing/Invoice.cs",
            Applies = false,
            Scan = new IdeScanNeed { Reason = IdeScanNeed.NewProject, Project = "src/Billing/Billing.csproj", Message = "m" },
        };

        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(report, IdeJsonContext.Default.IdeFileReport))!;

        Assert.Equal("new-project", (string?)json["scan"]!["reason"]);
    }
}
