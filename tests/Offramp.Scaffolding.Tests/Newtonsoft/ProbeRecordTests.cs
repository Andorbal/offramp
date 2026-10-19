using Offramp.Analysis.Compilations;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Scaffolding.Newtonsoft;

namespace Offramp.Scaffolding.Tests.Newtonsoft;

/// <summary>Probe result files and how a verification judges them (ADR 0067).</summary>
public sealed class ProbeRecordTests
{
    [Fact]
    public void A_result_file_parses_and_its_header_is_not_compared()
    {
        var seven = Record("returned", "{\"a\":1}", newtonsoft: "7.0.0.0");
        var thirteen = Record("returned", "{\"a\":1}", newtonsoft: "13.0.0.0");

        Assert.Equal("7.0.0.0", seven.Newtonsoft);
        Assert.Equal(ProbeStatus.Same, ProbeRecord.Compare(seven, thirteen));
        Assert.Null(ProbeRecord.Parse("not a probe result"));
    }

    [Theory]
    [InlineData("returned", "{\"a\":1}", "returned", "{\"a\":2}", ProbeStatus.Differs)]
    [InlineData("returned", "{\"a\":1}", "threw", "Newtonsoft.Json.JsonReaderException\n\nToo deep.", ProbeStatus.NowThrows)]
    [InlineData("threw", "System.InvalidCastException\n\nNo.", "returned", "{}", ProbeStatus.NoLongerThrows)]
    [InlineData("threw", "System.FormatException\n\nBad escape.", "threw", "System.FormatException\n\nBad \\u escape.", ProbeStatus.MessageOnly)]
    [InlineData("threw", "System.FormatException\n\nBad escape.", "threw", "Newtonsoft.Json.JsonReaderException\n\nBad escape.", ProbeStatus.Differs)]
    public void Observations_compare_by_outcome_and_body(string baselineOutcome, string baselineBody, string nowOutcome, string nowBody, ProbeStatus expected) =>
        Assert.Equal(expected, ProbeRecord.Compare(Record(baselineOutcome, baselineBody), Record(nowOutcome, nowBody)));

    [Fact]
    public void The_diff_lists_removed_lines_before_added_ones()
    {
        var diff = ProbeRecord.Diff("Money\n  Amount: 1\n  Currency: \"EUR\"", "Money\n  Amount: 1.0\n  Currency: \"EUR\"");

        Assert.Equal("-   Amount: 1\n+   Amount: 1.0", diff);
    }

    [Fact]
    public async Task A_self_hosted_test_with_no_old_deployment_uses_its_own_resolved_release()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var model = scanned.Outcome.Model!;
        var original = model.Projects.Single(p => p.Id == "src/Catalog/Catalog.csproj");
        var framework = CompilationLoader.PreferredTarget(original)!;
        var production = original with
        {
            Kind = Offramp.Core.Model.ProjectKind.Console,
            OutputType = "Exe",
            HostsTests = true,
            PackagesConfigPackages = [new Offramp.Core.Model.PackagesConfigPackage { Id = "Newtonsoft.Json", Version = "7.0.1" }],
            Resolved = new SortedDictionary<string, Offramp.Core.Model.ResolvedFramework>(StringComparer.Ordinal)
            {
                [framework] = new() { Packages = [new Offramp.Core.Model.ResolvedPackage { Id = "Newtonsoft.Json", Version = "13.0.4", Direct = true }] },
            },
        };

        Assert.Equal("13.0.0.0", ProbeRecorder.ExpectedIdentity(production, isSelfTestHost: true));
        Assert.Equal("7.0.0.0", ProbeRecorder.ExpectedIdentity(production, isSelfTestHost: false));
    }

    [Fact]
    [ProducesDiagnostic("OFR3763")]
    public async Task Only_a_changed_message_is_information()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var diagnostics = new DiagnosticBag();
        var request = new ProbeRunRequest
        {
            RepositoryRoot = scanned.Root,
            Model = scanned.Outcome.Model!,
            Config = new OfframpConfig(),
            Targets = [],
            Probes = [],
            Processes = Offramp.Core.Processes.ProcessRunner.Instance,
            Diagnostics = diagnostics,
        };

        ProbeRecorder.Report(request, [new ProbeOutcome { Probe = "A.B#1.entry.edge", TestProject = "tests/A.Tests/A.Tests.csproj", Runtime = "core", Status = ProbeStatus.MessageOnly }]);

        var reported = Assert.Single(diagnostics.ToSortedList());
        Assert.Equal("OFR3763", reported.Code);
        Assert.Equal(Severity.Info, reported.Severity);
    }

    private static ProbeRecord Record(string outcome, string body, string newtonsoft = "7.0.0.0") =>
        ProbeRecord.Parse($"# offramp-newtonsoft-probe 1\nprobe: A.B#1.entry.typical\nnewtonsoft: {newtonsoft}\noutcome: {outcome}\n---\n{body}\n")!;
}
