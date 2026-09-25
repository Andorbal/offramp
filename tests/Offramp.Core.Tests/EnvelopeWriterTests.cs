using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Output;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class EnvelopeWriterTests
{
    private static string Sample()
    {
        var bag = new DiagnosticBag(new Dictionary<string, SeverityOverride> { ["OFR0015"] = new(Severity.Info, "exported tree") });
        bag.Report(DiagnosticCatalog.OFR0050, "Unknown key 'verfy' is ignored. Did you mean 'verify'?",
            new DiagnosticLocation(File: "offramp.yml", Line: 2, Column: 1),
            [KeyValuePair.Create<string, JsonNode?>("key", "verfy")]);
        bag.Report(DiagnosticCatalog.OFR0015, "Not inside a git work tree; moves would use plain file moves.");
        var header = new EnvelopeHeader
        {
            Version = "0.1.0",
            Command = "doctor",
            Target = "net10.0",
            RepositoryRoot = "/abs/repo",
            Solution = "src/Monolith.sln",
            WorkspaceHash = null,
            StartedAt = EnvelopeHeader.FormatTimestamp(FakeTimeProvider.DefaultStart),
            DurationMs = 1234,
            EffectiveConfig = OfframpJson.SortKeys(ConfigLoader.Defaults())!,
        };
        var result = new JsonObject { ["answer"] = 42, ["items"] = new JsonArray("a", "b") };
        return EnvelopeWriter.Write(header, result, OfframpCoreJsonContext.Default.JsonObject, bag.ToSortedList());
    }

    [Fact]
    public Task Envelope_matches_the_snapshot()
    {
        var json = Sample();
        return Verify(json, extension: "json");
    }

    [Fact]
    public void Envelope_validates_against_the_schema()
    {
        SchemaAssert.Valid("envelope", Sample());
    }

    [Fact]
    public void Envelope_is_byte_identical_across_runs_and_ends_with_a_newline()
    {
        var first = Sample();
        Assert.Equal(first, Sample());
        Assert.EndsWith("}\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamps_are_utc_with_second_precision()
    {
        var local = new DateTimeOffset(2026, 9, 25, 22, 11, 4, 987, TimeSpan.FromHours(2));
        Assert.Equal("2026-09-25T20:11:04Z", EnvelopeHeader.FormatTimestamp(local));
    }
}
