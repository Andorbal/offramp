using Offramp.Analysis.DeadCode;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Analysis.Tests;

public sealed class DeadCodeTests
{
    [Fact]
    [ProducesDiagnostic("OFR3401")]
    public async Task Candidates_carry_the_confidence_their_evidence_allows()
    {
        var (result, bag) = await AnalyzeAsync();

        var all = result.Projects.SelectMany(p => p.Candidates).ToDictionary(c => c.Symbol);
        Assert.Equal(
            [
                ("DeadCode.App.Program", DeadCodeConfidence.Low),
                ("DeadCode.Contracts.LegacyDto", DeadCodeConfidence.Medium),
                ("DeadCode.Core.InternalCache", DeadCodeConfidence.High),
                ("DeadCode.Core.InvoiceHandler", DeadCodeConfidence.Low),
                ("DeadCode.Core.LegacyExporter", DeadCodeConfidence.High),
                ("DeadCode.Core.OrderService.Archive(int)", DeadCodeConfidence.High),
                ("DeadCode.Core.ReportPlugin", DeadCodeConfidence.Low),
                ("DeadCode.Core.Snapshot", DeadCodeConfidence.Low),
            ],
            all.Values.Select(c => (c.Symbol, c.Confidence)).OrderBy(c => c.Symbol, StringComparer.Ordinal));
        Assert.Contains(all["DeadCode.Core.ReportPlugin"].Evidence, e => e.StartsWith("the name appears in a string or resource at src/App/Program.cs:", StringComparison.Ordinal));
        Assert.Contains(all["DeadCode.Core.InvoiceHandler"].Evidence, e => e.Contains("registers types by convention (Registry.Scan at src/App/Program.cs:", StringComparison.Ordinal));
        Assert.Contains(all["DeadCode.Core.Snapshot"].Evidence, e => e.StartsWith("[Serializable]", StringComparison.Ordinal));
        Assert.Contains("entry point", all["DeadCode.App.Program"].Evidence);
        Assert.Equal("public in a packable assembly (IsPackable): other repositories may use it", all["DeadCode.Contracts.LegacyDto"].Evidence[0]);
        Assert.Equal(["internal"], all["DeadCode.Core.InternalCache"].Evidence);

        // Used code, and members of unused types, are not candidates.
        Assert.DoesNotContain(all.Keys, k => k.Contains("OrderService.Total", StringComparison.Ordinal) || k.Contains("InternalCache.Put", StringComparison.Ordinal) || k.Contains("FixedClock", StringComparison.Ordinal));
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR3401" && d.Project == "src/Core/Core.csproj");
    }

    [Fact]
    public async Task The_removable_lines_are_the_high_confidence_declarations()
    {
        var (result, _) = await AnalyzeAsync();

        var core = Assert.Single(result.Projects, p => p.Project == "src/Core/Core.csproj");
        var lines = core.Candidates.Where(c => c.Confidence == DeadCodeConfidence.High).ToDictionary(c => c.Symbol, c => (c.File, c.Line, c.Loc));
        Assert.Equal(("src/Core/OrderService.cs", 11, 5), lines["DeadCode.Core.OrderService.Archive(int)"]);
        Assert.Equal(("src/Core/LegacyExporter.cs", 7, 14), lines["DeadCode.Core.LegacyExporter"]);
        Assert.Equal(("src/Core/InternalCache.cs", 5, 9), lines["DeadCode.Core.InternalCache"]);
        Assert.Equal(28, core.Loc.High);
        Assert.Equal(28, result.Summary.RemovableLoc);
        Assert.Equal((8, 3, 1, 4), (result.Summary.Candidates, result.Summary.High, result.Summary.Medium, result.Summary.Low));
    }

    [Fact]
    [ProducesDiagnostic("OFR3402")]
    public async Task With_tests_included_code_only_tests_use_is_reported_separately()
    {
        var (result, bag) = await AnalyzeAsync(r => r with { IncludeTests = true });

        var core = Assert.Single(result.Projects, p => p.Project == "src/Core/Core.csproj");
        var clock = Assert.Single(core.TestOnly);
        Assert.Equal(("DeadCode.Core.FixedClock", "class", 6), (clock.Symbol, clock.Kind, clock.Line));
        Assert.Equal(["src/Core.Tests/Core.Tests.csproj"], clock.Tests);
        Assert.DoesNotContain(core.Candidates, c => c.Symbol.Contains("FixedClock", StringComparison.Ordinal));
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR3402" && d.Message.StartsWith("DeadCode.Core.FixedClock is used only by src/Core.Tests/Core.Tests.csproj", StringComparison.Ordinal));
        Assert.Equal(1, result.Summary.TestOnly);
    }

    [Fact]
    public async Task Scope_and_minimum_confidence_filter_and_external_consumers_lower_confidence()
    {
        var (publicOnly, _) = await AnalyzeAsync(r => r with { Scope = "public" });
        var (confident, _) = await AnalyzeAsync(r => r with { MinConfidence = DeadCodeConfidence.High, ExternalConsumers = ["Core"] });

        var publicSymbols = publicOnly.Projects.SelectMany(p => p.Candidates).Select(c => c.Symbol).ToList();
        Assert.DoesNotContain("DeadCode.Core.InternalCache", publicSymbols);
        Assert.DoesNotContain("DeadCode.App.Program", publicSymbols);
        Assert.Contains("DeadCode.Core.LegacyExporter", publicSymbols);

        // Core is now consumed elsewhere: its public candidates drop to medium, below the minimum.
        Assert.Equal(["DeadCode.Core.InternalCache"], confident.Projects.SelectMany(p => p.Candidates).Select(c => c.Symbol));
    }

    [Fact]
    public async Task Analysis_is_deterministic()
    {
        var (first, _) = await AnalyzeAsync();
        var (second, _) = await AnalyzeAsync();

        Assert.Equal(
            first.Projects.SelectMany(p => p.Candidates).Select(c => $"{c.Symbol} {c.Confidence} {c.File}:{c.Line} {c.Loc} {string.Join("|", c.Evidence)}"),
            second.Projects.SelectMany(p => p.Candidates).Select(c => $"{c.Symbol} {c.Confidence} {c.File}:{c.Line} {c.Loc} {string.Join("|", c.Evidence)}"));
    }

    private static async Task<(DeadCodeResult Result, DiagnosticBag Diagnostics)> AnalyzeAsync(Func<DeadCodeRequest, DeadCodeRequest>? customize = null)
    {
        var fixture = await ScannedFixtures.GetAsync("dead-code");
        var bag = new DiagnosticBag();
        var request = new DeadCodeRequest
        {
            RepositoryRoot = fixture.Root,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            Diagnostics = bag,
        };
        return (DeadCodeAnalyzer.Analyze(customize?.Invoke(request) ?? request), bag);
    }
}
