using Offramp.Core.Diagnostics;
using Offramp.Core.Progress;
using Offramp.Fixtures;
using Offramp.Refactoring.Moves;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#moves-from-the-editor: move plan + move apply, pure, verified, and no scan between moves.</summary>
public sealed class MoveTests
{
    [Fact]
    public async Task A_move_is_a_staged_rename_verified_by_a_build_and_the_next_move_needs_no_scan()
    {
        var fixture = await ScannedFixtures.ScanAsync(Engines.Fixture);
        using var repository = fixture.Repository;
        using var engine = await Engines.CreateAsync(fixture);
        var original = File.ReadAllBytes(repository.Directory.Combine("src", "Foo", "Pricing", "PriceCalculator.cs"));

        var diagnostics = new DiagnosticBag();
        var plan = engine.PlanMove("src/Foo/Pricing/PriceCalculator.cs", Engines.ModernF, diagnostics);
        Assert.NotNull(plan);
        Assert.Empty(plan.ProjectEdits);
        var outcome = await engine.ApplyMoveAsync(plan, diagnostics, NullProgressSink.Instance, CancellationToken.None);

        Assert.True(outcome.Result.Applied, string.Join("\n", diagnostics.ToSortedList().Select(d => d.Message)));
        Assert.True(Assert.Single(outcome.Result.Verifications).Passed);
        Assert.Equal(original, File.ReadAllBytes(repository.Directory.Combine("src", "ModernF", "Pricing", "PriceCalculator.cs")));
        var status = (await repository.GitAsync("status", "--porcelain")).StandardOutput;
        Assert.Contains("R  src/Foo/Pricing/PriceCalculator.cs -> src/ModernF/Pricing/PriceCalculator.cs", status, StringComparison.Ordinal);

        // OrderService used PriceCalculator, which is in ModernF now: it can follow, without a scan.
        var order = await engine.ReportAsync("src/Foo/Orders/OrderService.cs", CancellationToken.None);
        Assert.True(order.Moves.Single(m => m.To == Engines.ModernF).Movable);
        Assert.Equal("OFR2103", order.Moves.Single(m => m.To == Engines.Shared).Code);
        Assert.Null(engine.Staleness());
        var next = engine.PlanMove("src/Foo/Orders/OrderService.cs", Engines.ModernF, new DiagnosticBag());
        Assert.Equal(["src/ModernF/Orders/OrderService.cs"], next!.Moves.Select(m => m.To));
    }

    [Fact]
    [ProducesDiagnostic("OFR0002")]
    public async Task A_reference_the_move_adds_does_not_make_the_model_stale_but_someone_elses_edit_does()
    {
        var fixture = await ScannedFixtures.ScanAsync(Engines.Fixture);
        using var repository = fixture.Repository;
        using var engine = await Engines.CreateAsync(fixture, Engines.LegacyMapped);
        var diagnostics = new DiagnosticBag();

        var plan = engine.PlanMove("src/Legacy/Greeter.cs", Engines.ModernF, diagnostics)!;
        Assert.Equal([(Engines.Legacy, ProjectEditKind.AddProjectReference, Engines.ModernF)], plan.ProjectEdits.Select(e => (e.Project, e.Kind, e.Value)));
        Assert.Contains("Legacy gets a project reference to ModernF.", Lsp.OfframpLanguageServer.Confirmation(plan), StringComparison.Ordinal);
        var outcome = await engine.ApplyMoveAsync(plan, diagnostics, NullProgressSink.Instance, CancellationToken.None);
        Assert.True(outcome.Result.Applied, string.Join("\n", diagnostics.ToSortedList().Select(d => d.Message)));

        Assert.Null(engine.Staleness());
        var welcome = await engine.ReportAsync("src/Legacy/Welcome.cs", CancellationToken.None);
        Assert.True(Assert.Single(welcome.Moves).Movable);
        Assert.NotNull(engine.PlanMove("src/Legacy/Welcome.cs", Engines.ModernF, new DiagnosticBag()));

        repository.Directory.Write("src/ModernF/ModernF.csproj", repository.Directory.Read("src/ModernF/ModernF.csproj").Replace("<LangVersion>latest</LangVersion>", "<LangVersion>12</LangVersion>", StringComparison.Ordinal));
        var stale = new DiagnosticBag();
        Assert.Null(engine.PlanMove("src/Legacy/Welcome.cs", Engines.ModernF, stale));
        Assert.Equal("OFR0002", Assert.Single(stale.ToSortedList()).Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR6008")]
    public async Task Unsaved_changes_or_a_file_that_cannot_move_stop_the_move()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        using var engine = await Engines.CreateAsync(fixture);
        const string file = "src/Foo/Pricing/PriceCalculator.cs";

        engine.Workspace.SetOpenDocument(file, fixture.Repository.Directory.Read(file) + "// typing\n");
        var unsaved = new DiagnosticBag();
        Assert.Null(engine.PlanMove(file, Engines.ModernF, unsaved));
        engine.Workspace.CloseDocument(file);
        var blocked = new DiagnosticBag();
        Assert.Null(engine.PlanMove("src/Foo/Orders/OrderService.cs", Engines.ModernF, blocked));

        Assert.Equal("OFR6008", Assert.Single(unsaved.ToSortedList()).Code);
        Assert.Contains(blocked.ToSortedList(), d => d.Code == "OFR2101");
    }
}
