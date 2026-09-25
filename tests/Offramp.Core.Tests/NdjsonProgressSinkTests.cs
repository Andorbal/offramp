using System.Text.Json.Nodes;
using Offramp.Core.Progress;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class NdjsonProgressSinkTests
{
    [Fact]
    public void Emits_phase_progress_log_and_done_events_one_per_line()
    {
        var writer = new StringWriter();
        var time = new FakeTimeProvider();
        var sink = new NdjsonProgressSink(writer, time);

        using (var phase = sink.BeginPhase("Loading compilations", 2, 5))
        {
            phase.Report(143, 612, "src/Foo/Foo.csproj");
            time.Advance(TimeSpan.FromSeconds(40.211));
            sink.Log(ProgressLevel.Info, "Reusing cached nupkg metadata for 1,204 packages");
        }

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
        [
            """{"event":"phase","name":"Loading compilations","index":2,"of":5}""",
            """{"event":"progress","phase":"Loading compilations","current":143,"total":612,"item":"src/Foo/Foo.csproj"}""",
            """{"event":"log","level":"info","message":"Reusing cached nupkg metadata for 1,204 packages"}""",
            """{"event":"done","phase":"Loading compilations","durationMs":40211}""",
        ], lines);
        foreach (var line in lines)
        {
            SchemaAssert.Valid("progress", line);
        }
    }

    [Fact]
    public void Progress_is_throttled_to_ten_per_second_but_the_final_update_is_kept()
    {
        var writer = new StringWriter();
        var time = new FakeTimeProvider();
        var sink = new NdjsonProgressSink(writer, time);

        using (var phase = sink.BeginPhase("Scanning", 1, 1))
        {
            for (var i = 1; i <= 100; i++)
            {
                phase.Report(i, 100);
                time.Advance(TimeSpan.FromMilliseconds(10));
            }
        }

        var progress = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!)
            .Where(n => n["event"]!.GetValue<string>() == "progress")
            .Select(n => n["current"]!.GetValue<int>())
            .ToList();

        Assert.Equal(1, progress[0]);
        Assert.Equal(100, progress[^1]);
        Assert.InRange(progress.Count, 10, 12);
    }

    [Fact]
    public void Debug_logs_appear_only_when_verbose()
    {
        var quiet = new StringWriter();
        new NdjsonProgressSink(quiet, new FakeTimeProvider()).Log(ProgressLevel.Debug, "detail");
        Assert.Equal("", quiet.ToString());

        var verbose = new StringWriter();
        new NdjsonProgressSink(verbose, new FakeTimeProvider(), verbose: true).Log(ProgressLevel.Debug, "detail");
        Assert.Equal("{\"event\":\"log\",\"level\":\"debug\",\"message\":\"detail\"}\n", verbose.ToString());
    }
}
