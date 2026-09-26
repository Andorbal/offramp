using Offramp.Fixtures;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Tests;

public sealed class BinlogReaderTests
{
    /// <summary>
    /// Regression: MSBuild.StructuredLogger hands each read's result through a static field,
    /// so concurrent scans in one process (tests, the MCP server) lost each other's builds.
    /// The race window is narrow: the CLI test suite hit it in 3 of 20 runs before reads were
    /// serialized, this stress test more rarely; it guards the behavior, the lock fixes it.
    /// </summary>
    [Fact]
    public async Task Concurrent_reads_of_a_log_all_see_every_evaluation()
    {
        var binlog = RepositoryFiles.Path("tests", "fixtures", "windows-only-build-steps", "msbuild.binlog");
        var expected = BinlogReader.Read(binlog).Evaluations.Count;

        // Readers start at staggered moments so reads keep finishing while others begin.
        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var seen = new List<int>();
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay((worker * 7 + i * 3) % 11);
                seen.Add(BinlogReader.Read(binlog).Evaluations.Count);
            }

            return seen;
        })));

        Assert.True(expected > 0);
        Assert.All(counts.SelectMany(c => c), c => Assert.Equal(expected, c));
    }

    [Fact]
    public void A_file_that_is_not_a_log_is_an_error_not_an_empty_build()
    {
        using var directory = new ScratchDirectory("binlog");
        var path = directory.Write("bad.binlog", "not a log");

        Assert.ThrowsAny<Exception>(() => BinlogReader.Read(path));
    }
}
