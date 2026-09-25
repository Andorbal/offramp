using Offramp.Core.Diagnostics;

namespace Offramp.Core.Tests;

public sealed class DiagnosticBagTests
{
    [Fact]
    public void Diagnostics_are_sorted_by_code_then_location_then_message()
    {
        var bag = new DiagnosticBag();
        bag.Report(DiagnosticCatalog.OFR0050, "b", new DiagnosticLocation(File: "z.yml", Line: 2));
        bag.Report(DiagnosticCatalog.OFR0050, "a", new DiagnosticLocation(File: "z.yml", Line: 1));
        bag.Report(DiagnosticCatalog.OFR0014, "git");
        bag.Report(DiagnosticCatalog.OFR0050, "c", new DiagnosticLocation(File: "a.yml", Line: 9));

        var ordered = bag.ToSortedList().Select(d => $"{d.Code}:{d.File}:{d.Line}:{d.Message}").ToList();

        Assert.Equal(
            ["OFR0014:::git", "OFR0050:a.yml:9:c", "OFR0050:z.yml:1:a", "OFR0050:z.yml:2:b"],
            ordered);
    }

    [Fact]
    public void Overrides_change_severity_mark_overridden_and_none_disables()
    {
        var bag = new DiagnosticBag(new Dictionary<string, SeverityOverride>
        {
            ["OFR0014"] = new(Severity.Error, "git is mandatory here"),
            ["OFR0015"] = new(null, "exported trees are fine"),
        });

        var promoted = bag.Report(DiagnosticCatalog.OFR0014, "no git");
        var disabled = bag.Report(DiagnosticCatalog.OFR0015, "no repo");
        var untouched = bag.Report(DiagnosticCatalog.OFR0016, "no config");

        Assert.NotNull(promoted);
        Assert.Equal(Severity.Error, promoted.Severity);
        Assert.True(promoted.Overridden);
        Assert.Null(disabled);
        Assert.False(untouched!.Overridden);
        Assert.Equal(new DiagnosticSummary(1, 0, 1), bag.Summary());
    }

    [Fact]
    public void Data_keys_are_sorted()
    {
        var bag = new DiagnosticBag();
        var diagnostic = bag.Report(DiagnosticCatalog.OFR0012, "old sdk", data:
        [
            KeyValuePair.Create<string, System.Text.Json.Nodes.JsonNode?>("target", "net10.0"),
            KeyValuePair.Create<string, System.Text.Json.Nodes.JsonNode?>("selected", "8.0.100"),
        ]);

        Assert.Equal(["selected", "target"], diagnostic!.Data.Keys);
        Assert.Equal("https://offramp.dev/diagnostics/OFR0012", diagnostic.Help);
    }

    [Theory]
    [InlineData(Severity.Error, FailOn.Error, true)]
    [InlineData(Severity.Warning, FailOn.Error, false)]
    [InlineData(Severity.Warning, FailOn.Warning, true)]
    [InlineData(Severity.Info, FailOn.Info, true)]
    [InlineData(Severity.Error, FailOn.Never, false)]
    public void Fail_on_thresholds(Severity severity, FailOn threshold, bool meets) =>
        Assert.Equal(meets, severity.Meets(threshold));
}
