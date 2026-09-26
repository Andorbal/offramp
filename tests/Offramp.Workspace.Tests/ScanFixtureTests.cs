using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Scanning;
using Offramp.Workspace.Store;

namespace Offramp.Workspace.Tests;

/// <summary>
/// Roadmap M1 acceptance: `scan` on every fixture produces a snapshot-tested
/// model on all three OSes.
/// </summary>
public sealed class ScanFixtureTests
{
    public static TheoryData<string> Fixtures => ["netfx-only", "dual-target", "cycle", "windows-only-build-steps", "versions", "tests-in-prod", "move-cases"];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Model_matches_the_snapshot_and_the_schema(string fixture)
    {
        var scanned = await ScannedFixtures.GetAsync(fixture);

        Assert.NotNull(scanned.Outcome.Model);
        SchemaAssert.Valid("workspace", scanned.ModelJson);
        await Verify(Scrub.Model(scanned.ModelJson, scanned.Root), extension: "json").UseParameters(fixture);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Result_and_ledger_match_their_schemas(string fixture)
    {
        var scanned = await ScannedFixtures.GetAsync(fixture);
        var result = scanned.Outcome.Result!;

        SchemaAssert.Valid("scan", OfframpJson.Serialize(result, WorkspaceJsonContext.Default.ScanResult));
        Assert.NotNull(result.LedgerSnapshot);
        SchemaAssert.Valid("ledger", File.ReadAllText(Path.Combine(scanned.Root, result.LedgerSnapshot!)));
        await Verify(Scrub.Model(OfframpJson.Serialize(result, WorkspaceJsonContext.Default.ScanResult), scanned.Root), extension: "json")
            .UseParameters(fixture);
    }

    [Fact]
    public async Task Dual_target_is_classified_and_ordered()
    {
        var model = (await ScannedFixtures.GetAsync("dual-target")).Outcome.Model!;

        Assert.Equal(
            [("src/Contracts/Contracts.csproj", FrameworkClass.Standard), ("src/Shared/Shared.csproj", FrameworkClass.Dual), ("src/Tool/Tool.csproj", FrameworkClass.Modern)],
            model.Projects.Select(p => (p.Id, p.FrameworkClass)));
        Assert.Equal(["src/Contracts/Contracts.csproj", "src/Shared/Shared.csproj", "src/Tool/Tool.csproj"], model.Graph.TopologicalOrder);
        var shared = model.Projects.Single(p => p.Name == "Shared");
        Assert.Equal(["net48", "net10.0"], shared.TargetFrameworks);
        Assert.Contains("NETFRAMEWORK", shared.DefineConstants["net48"]);
        Assert.DoesNotContain("NETFRAMEWORK", shared.DefineConstants["net10.0"]);
        Assert.Equal(["net10.0", "net48"], shared.CompilerCalls.Keys);
        Assert.Contains(shared.AssemblyReferences, r => r.Name == "System.Web" && r.Kind == AssemblyReferenceKind.Framework);
        Assert.DoesNotContain(shared.AssemblyReferences, r => r.Name == "mscorlib");
        Assert.Equal(ProjectKind.Console, model.Projects.Single(p => p.Name == "Tool").Kind);
        Assert.Equal(["13.0.3"], model.Packages["Newtonsoft.Json"].Versions.Keys);
    }

    [Fact]
    [ProducesDiagnostic("OFR0120")]
    public async Task Cycle_through_a_hint_path_is_found()
    {
        var scanned = await ScannedFixtures.GetAsync("cycle");
        var model = scanned.Outcome.Model!;

        Assert.Equal([["src/Alpha/Alpha.csproj", "src/Beta/Beta.csproj"]], model.Graph.Cycles);
        Assert.Contains(model.Graph.Edges, e => e.From == "src/Beta/Beta.csproj" && e.To == "src/Alpha/Alpha.csproj" && e.Kind == GraphEdgeKind.Assembly);
        Assert.Contains(model.Graph.Edges, e => e.From == "src/Alpha/Alpha.csproj" && e.To == "src/Beta/Beta.csproj" && e.Kind == GraphEdgeKind.Project);
        var cycle = scanned.Diagnostics.ToSortedList().Single(d => d.Code == "OFR0120");
        Assert.Equal("Project reference cycle: src/Alpha/Alpha.csproj → src/Beta/Beta.csproj → src/Alpha/Alpha.csproj.", cycle.Message);
    }

    [Fact]
    [ProducesDiagnostic("OFR0101")]
    [ProducesDiagnostic("OFR0104")]
    [ProducesDiagnostic("OFR0110")]
    [ProducesDiagnostic("OFR0111")]
    [ProducesDiagnostic("OFR0112")]
    [ProducesDiagnostic("OFR0113")]
    [ProducesDiagnostic("OFR0114")]
    [ProducesDiagnostic("OFR0115")]
    [ProducesDiagnostic("OFR0130")]
    [ProducesDiagnostic("OFR0132")]
    public async Task Windows_only_build_steps_are_detected_from_a_log_captured_elsewhere()
    {
        var scanned = await ScannedFixtures.GetAsync("windows-only-build-steps");
        var model = scanned.Outcome.Model!;
        var codes = scanned.Diagnostics.ToSortedList().Select(d => d.Code).ToHashSet();

        Assert.Equal(["sgen", "entity-deploy", "t4", "fakes", "build-event"], model.Projects.Single(p => p.Name == "Soap").WindowsOnlyBuildSteps);
        Assert.Equal(["com"], model.Projects.Single(p => p.Name == "Office").WindowsOnlyBuildSteps);
        foreach (var code in new[] { "OFR0101", "OFR0104", "OFR0110", "OFR0111", "OFR0112", "OFR0113", "OFR0114", "OFR0115", "OFR0130", "OFR0132" })
        {
            Assert.Contains(code, codes);
        }

        Assert.Equal(model.Projects.Select(p => p.Id), model.Projects.Where(p => p.Partial).Select(p => p.Id));
        Assert.Contains(scanned.Outcome.Result!.WindowsOnlyBuildSteps, w => w.Project == "src/Database/Database.sqlproj" && w.Steps.SequenceEqual(["ssdt"]));
        Assert.False(scanned.Outcome.Result.BuildSucceeded);
        Assert.DoesNotContain("/home/", scanned.ModelJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rescanning_the_same_log_is_byte_identical()
    {
        var scanned = await ScannedFixtures.ScanAsync("netfx-only");
        using var _ = scanned.Repository;
        var first = scanned.ModelJson;

        var again = await ScanRunner.RunAsync(
            ScannedFixtures.Request(scanned.Root, new DiagnosticBag()) with { NoBuild = true }, CancellationToken.None);

        Assert.Equal(Core.Paths.RepoPaths.ToRepositoryRelative(scanned.Root, scanned.WorkspacePath), again.Result!.Model);
        Assert.Equal(first, scanned.ModelJson);
    }

    [Fact]
    [ProducesDiagnostic("OFR0002")]
    public async Task Changing_a_project_file_makes_the_model_stale_and_if_stale_rescans()
    {
        var scanned = await ScannedFixtures.ScanAsync("netfx-only");
        using var _ = scanned.Repository;

        var fresh = await ScanRunner.RunAsync(
            ScannedFixtures.Request(scanned.Root, new DiagnosticBag()) with { IfStale = true }, CancellationToken.None);
        Assert.True(fresh.Result!.UpToDate);

        var project = Path.Combine(scanned.Root, "src", "Legacy.Core", "Legacy.Core.csproj");
        File.AppendAllText(project, "<!-- touched -->\n");
        var bag = new DiagnosticBag();
        var loaded = WorkspaceStore.LoadForCommand(scanned.WorkspacePath, scanned.Root, new(), bag, failOnStale: false);
        Assert.NotNull(loaded);
        var stale = Assert.Single(bag.ToSortedList());
        Assert.Equal("OFR0002", stale.Code);
        Assert.Equal(Severity.Warning, stale.Severity);
        Assert.Contains("src/Legacy.Core/Legacy.Core.csproj", stale.Message, StringComparison.Ordinal);

        var strict = new DiagnosticBag();
        WorkspaceStore.LoadForCommand(scanned.WorkspacePath, scanned.Root, new(), strict, failOnStale: true);
        Assert.Equal(Severity.Error, strict.ToSortedList().Single().Severity);

        var rescanned = await ScanRunner.RunAsync(
            ScannedFixtures.Request(scanned.Root, new DiagnosticBag()) with { IfStale = true, NoBuild = true }, CancellationToken.None);
        Assert.False(rescanned.Result!.UpToDate);
        var after = new DiagnosticBag();
        WorkspaceStore.LoadForCommand(scanned.WorkspacePath, scanned.Root, new(), after, failOnStale: false);
        Assert.Equal(0, after.Count);
    }

    [Fact]
    [ProducesDiagnostic("OFR0001")]
    public void A_missing_model_is_reported()
    {
        using var dir = new ScratchDirectory("nomodel");
        var bag = new DiagnosticBag();

        Assert.Null(WorkspaceStore.LoadForCommand(Path.Combine(dir.Path, ".offramp", "workspace.json"), dir.Path, new(), bag, failOnStale: false));
        Assert.Equal("OFR0001", bag.ToSortedList().Single().Code);
    }

    /// <summary>
    /// The logs of a build made in another checkout (standing in for a Windows
    /// agent) produce the same model as a native scan, once scrubbed.
    /// </summary>
    [Fact]
    public async Task Logs_captured_in_another_checkout_produce_the_native_model()
    {
        var native = await ScannedFixtures.ScanAsync("dual-target");
        using var _ = native.Repository;
        var nativeModel = native.ModelJson;
        var captured = await ScannedFixtures.ScanAsync("dual-target");
        using var __ = captured.Repository;
        var binlog = Path.Combine(captured.Root, ".offramp", ScanRunner.BinlogFileName);
        var complog = Path.Combine(captured.Root, ".offramp", ScanRunner.ComplogFileName);

        var bag = new DiagnosticBag();
        var fromLogs = await ScanRunner.RunAsync(
            ScannedFixtures.Request(native.Root, bag) with { BinlogPath = binlog, ComplogPath = complog }, CancellationToken.None);

        Assert.NotNull(fromLogs.Model);
        Assert.Equal(Comparable(nativeModel, native.Root), Comparable(native.ModelJson, native.Root));
        Assert.DoesNotContain(bag.ToSortedList(), d => d.Severity >= Severity.Warning);
    }

    /// <summary>
    /// Roadmap M1 acceptance: the binary and compiler logs of <c>dual-target</c> built on
    /// Windows (CI's windows-capture job) produce the native model, once scrubbed.
    /// </summary>
    [Fact]
    public async Task Logs_captured_on_windows_produce_the_native_model()
    {
        var capture = System.Environment.GetEnvironmentVariable("OFFRAMP_WINDOWS_CAPTURE");
        if (string.IsNullOrEmpty(capture))
        {
            Assert.Skip("OFFRAMP_WINDOWS_CAPTURE is not set; CI's windows-capture job provides the logs.");
        }

        var native = await ScannedFixtures.GetAsync("dual-target");
        using var copy = await FixtureRepository.CreateAsync("dual-target");
        var restore = await ProcessRunner.Instance.RunAsync(
            new ProcessSpec("dotnet", ["restore", "DualTarget.slnx", "-nologo"]) { WorkingDirectory = copy.Path }, CancellationToken.None);
        Assert.True(restore.Succeeded, restore.StandardOutput + restore.StandardError);

        var bag = new DiagnosticBag();
        var outcome = await ScanRunner.RunAsync(
            ScannedFixtures.Request(copy.Path, bag) with
            {
                BinlogPath = Path.Combine(capture, ScanRunner.BinlogFileName),
                ComplogPath = Path.Combine(capture, ScanRunner.ComplogFileName),
            },
            CancellationToken.None);

        Assert.NotNull(outcome.Model);
        Assert.StartsWith("win", outcome.Model.Sdk.Os, StringComparison.Ordinal);
        Assert.DoesNotContain(bag.ToSortedList(), d => d.Severity >= Severity.Warning);
        Assert.Equal(
            Comparable(native.ModelJson, native.Root),
            Comparable(File.ReadAllText(Path.Combine(copy.Path, ".offramp", "workspace.json")), copy.Path));
    }

    [Fact]
    [ProducesDiagnostic("OFR0103")]
    public async Task A_compiler_log_alone_gives_the_compiler_view_of_the_model()
    {
        var native = await ScannedFixtures.GetAsync("dual-target");
        using var copy = await FixtureRepository.CreateAsync("dual-target");
        var complog = Path.Combine(native.Root, ".offramp", ScanRunner.ComplogFileName);
        var bag = new DiagnosticBag();

        var outcome = await ScanRunner.RunAsync(ScannedFixtures.Request(copy.Path, bag) with { ComplogPath = complog }, CancellationToken.None);

        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR0103");
        var reduced = outcome.Model!;
        var full = native.Outcome.Model!;
        Assert.Equal(WorkspaceSourceKind.Complog, reduced.Source.Kind);
        Assert.Equal(full.Projects.Select(p => p.Id), reduced.Projects.Select(p => p.Id));
        foreach (var (f, r) in full.Projects.Zip(reduced.Projects))
        {
            Assert.Equal(f.TargetFrameworks, r.TargetFrameworks);
            Assert.Equal(f.FrameworkClass, r.FrameworkClass);
            Assert.Equal(f.Compile, r.Compile);
            Assert.Equal(f.ProjectReferences, r.ProjectReferences);
            Assert.Equal(f.AssemblyName, r.AssemblyName);
            Assert.Equal(f.Kind, r.Kind);
        }

        Assert.Equal(full.Graph.TopologicalOrder, reduced.Graph.TopologicalOrder);
        SchemaAssert.Valid("workspace", File.ReadAllText(Path.Combine(copy.Path, ".offramp", "workspace.json")));
    }

    /// <summary>A scrubbed model without its source (the logs it was read from differ by design).</summary>
    private static string Comparable(string modelJson, string root)
    {
        var node = JsonNode.Parse(Scrub.Model(modelJson, root))!.AsObject();
        node.Remove("source");
        return OfframpJson.Format(node);
    }
}
