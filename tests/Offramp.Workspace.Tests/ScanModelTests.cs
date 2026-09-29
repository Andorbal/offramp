using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Scanning;

namespace Offramp.Workspace.Tests;

/// <summary>What the workspace model records, and that the same inputs give the same model.</summary>
public sealed class ScanModelTests
{
    /// <summary>
    /// SmartStoreNET P1 #8: the compiler log lists calls in the order a parallel build finished them, so a call's
    /// position differed between two scans; the model names calls by project and target framework instead.
    /// </summary>
    [Fact]
    public void Compiler_calls_are_named_the_same_whatever_order_the_log_lists_them_in()
    {
        var mapper = CapturePathMapper.Local("/repo");
        CompilerCallInfo[] calls =
        [
            new(0, "/repo/src/A/A.csproj", "net48", IsCSharp: true),
            new(1, "/repo/src/A/A.csproj", "net10.0", IsCSharp: true),
            new(2, "/repo/src/Legacy/Legacy.csproj", null, IsCSharp: true),
        ];
        CompilerCallInfo[] reordered = [calls[2] with { Index = 0 }, calls[0] with { Index = 1 }, calls[1] with { Index = 2 }];

        var first = ScanRunner.MapCalls(calls, mapper, ".offramp/build.complog");
        var second = ScanRunner.MapCalls(reordered, mapper, ".offramp/build.complog");

        Assert.Equal(first.OrderBy(e => e.Key), second.OrderBy(e => e.Key));
        Assert.Equal(new Core.Model.CompilerCallRef(".offramp/build.complog", "src/Legacy/Legacy.csproj", null), first[("src/Legacy/Legacy.csproj", "")]);
    }

    /// <summary>
    /// SmartStoreNET P1 #8, NHibernate P2: two full scans of the same tree wrote different models (the built log's
    /// hash, and compiler-call positions); now they are byte-identical.
    /// </summary>
    [Fact]
    public async Task Two_full_scans_of_the_same_tree_write_the_same_model()
    {
        var scanned = await ScannedFixtures.ScanAsync("netfx-only");
        using var _ = scanned.Repository;
        var first = scanned.ModelJson;

        var again = await ScanRunner.RunAsync(ScannedFixtures.Request(scanned.Root, new DiagnosticBag()), CancellationToken.None);

        Assert.Equal(ScanFailure.None, again.Failure);
        Assert.Null(again.Model!.Source.Sha256);
        Assert.Equal(first, scanned.ModelJson);
    }
}
