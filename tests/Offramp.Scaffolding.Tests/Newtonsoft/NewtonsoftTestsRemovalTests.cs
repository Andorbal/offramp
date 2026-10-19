using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding.Newtonsoft;

namespace Offramp.Scaffolding.Tests.Newtonsoft;

public sealed class NewtonsoftTestsRemovalTests
{
    [Fact]
    [ProducesDiagnostic("OFR3742")]
    public async Task Remove_is_dry_run_and_keeps_files_without_ownership_markers()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy");
        var generated = Generate(scanned);
        ApplyCreates(scanned.Root, generated.ChangeSet!);
        var folder = Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization");
        var foreign = Path.Combine(folder, "OrdersSerializeProbes.cs");
        File.WriteAllText(foreign, "// user-owned\n");
        var input = Path.Combine(folder, "Inputs", "payload.json");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.WriteAllText(input, "{}\n");
        var baseline = Path.Combine(folder, "Baseline", "core", "probe.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "# offramp-newtonsoft-probe 1\nprobe: id\nnewtonsoft: 7.0.0.0\noutcome: returned\n---\nvalue\n");

        var diagnostics = new DiagnosticBag();
        var (result, changeSet) = NewtonsoftTestsGenerator.Remove(scanned.Root, scanned.Outcome.Model!, generated.Result.Targets, diagnostics);

        Assert.Equal("remove", result.Mode);
        Assert.False(result.Applied);
        Assert.Contains(result.Files, file => file.Path.EndsWith("/OrdersSerializeProbes.cs", StringComparison.Ordinal) && file.Action == "kept");
        Assert.Contains(result.Files, file => file.Path.EndsWith("/OfframpProbe.cs", StringComparison.Ordinal) && file.Action == "delete");
        Assert.DoesNotContain(changeSet!.Deletes, file => file.Path.EndsWith("/OrdersSerializeProbes.cs", StringComparison.Ordinal));
        Assert.Contains(changeSet.Deletes, file => file.Path.EndsWith("/OfframpProbe.cs", StringComparison.Ordinal));
        Assert.Contains(changeSet.Deletes, file => file.Path.EndsWith("/Inputs/payload.json", StringComparison.Ordinal));
        Assert.Contains(changeSet.Deletes, file => file.Path.EndsWith("/Baseline/core/probe.txt", StringComparison.Ordinal));
        Assert.Contains(diagnostics.ToSortedList(), diagnostic => diagnostic.Code == "OFR3742" && diagnostic.File == "tests/Orders.Tests/NewtonsoftCharacterization/OrdersSerializeProbes.cs");
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(Path.Combine(folder, "OfframpProbe.cs")));
    }

    [Fact]
    public async Task Remove_deletes_files_and_legacy_compile_items_with_a_rollback_journal()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy");
        var generated = Generate(scanned);
        ApplyCreates(scanned.Root, generated.ChangeSet!);
        var (result, changeSet) = NewtonsoftTestsGenerator.Remove(scanned.Root, scanned.Outcome.Model!, generated.Result.Targets, new DiagnosticBag());
        Assert.NotNull(changeSet);

        var applier = new ChangeSetApplier(scanned.Root, new Offramp.Core.Git.GitService(Offramp.Core.Processes.ProcessRunner.Instance));
        var journal = await applier.ApplyAsync(changeSet!, "newtonsoft tests remove", new FakeTimeProvider().GetUtcNow(), CancellationToken.None);
        var folder = Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization");
        Assert.False(File.Exists(Path.Combine(folder, "OfframpProbe.cs")));
        Assert.DoesNotContain("OfframpProbe.cs", File.ReadAllText(Path.Combine(scanned.Root, "tests", "Orders.Tests", "Orders.Tests.csproj")), StringComparison.Ordinal);
        Assert.Contains(applier.Read(journal).Steps, step => step.Kind == JournalStepKind.Delete);

        await applier.RollbackAsync(journal, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(folder, "OfframpProbe.cs")));
        Assert.Contains("OrderCodecTests.cs", File.ReadAllText(Path.Combine(scanned.Root, "tests", "Orders.Tests", "Orders.Tests.csproj")), StringComparison.Ordinal);
    }

    private static (NewtonsoftTestsResult Result, ChangeSet? ChangeSet) Generate(ScannedFixture scanned) => NewtonsoftTestsGenerator.Generate(new NewtonsoftTestsRequest
    {
        RepositoryRoot = scanned.Root,
        Model = scanned.Outcome.Model!,
        Diagnostics = new DiagnosticBag(),
    });

    private static void ApplyCreates(string root, ChangeSet changeSet)
    {
        foreach (var create in changeSet.Creates)
        {
            var path = Path.Combine(root, create.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, create.Content);
        }
    }
}
