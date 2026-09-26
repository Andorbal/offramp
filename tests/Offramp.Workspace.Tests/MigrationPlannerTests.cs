using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Model;
using Offramp.Workspace.Planning;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

public sealed class MigrationPlannerTests
{
    // Contracts (standard) ← Core ← Data ← Web (web), Data ← Data.Tests (test); Core ← Tool (console);
    // Contracts ← Api (dual); legacy/A ↔ legacy/B (cycle) ← Report.
    private static readonly WorkspaceModel Model = ModelOf(
        Project("lib/Contracts/Contracts.csproj") with { FrameworkClass = FrameworkClass.Standard },
        Project("src/Core/Core.csproj", references: ["lib/Contracts/Contracts.csproj"]),
        Project("src/Data/Data.csproj", references: ["src/Core/Core.csproj"]),
        Project("src/Web/Web.csproj", references: ["src/Data/Data.csproj"], kind: ProjectKind.Web),
        Project("src/Tool/Tool.csproj", references: ["src/Core/Core.csproj"], kind: ProjectKind.Console),
        Project("src/Api/Api.csproj", references: ["lib/Contracts/Contracts.csproj"], kind: ProjectKind.Web) with { FrameworkClass = FrameworkClass.Dual },
        Project("legacy/A/A.csproj", references: ["legacy/B/B.csproj"]),
        Project("legacy/B/B.csproj", references: ["legacy/A/A.csproj"]),
        Project("src/Report/Report.csproj", references: ["legacy/A/A.csproj"]),
        Project("tests/Data.Tests/Data.Tests.csproj", references: ["src/Data/Data.csproj"], kind: ProjectKind.Test));

    [Fact]
    public void Order_is_by_wave_then_blast_radius_then_path()
    {
        var plan = MigrationPlanner.Plan(Model, null, false, []);

        Assert.Equal(
            [
                ("lib/Contracts/Contracts.csproj", 0), ("src/Api/Api.csproj", 0),
                ("src/Core/Core.csproj", 1), ("legacy/A/A.csproj", 1), ("legacy/B/B.csproj", 1),
                ("src/Data/Data.csproj", 2), ("src/Report/Report.csproj", 2), ("src/Tool/Tool.csproj", 2),
                ("src/Web/Web.csproj", 3), ("tests/Data.Tests/Data.Tests.csproj", 3),
            ],
            plan.Order.Select(e => (e.Project, e.Wave)));
        Assert.Equal(new PlanCounts(10, 2, 1, 7, 3), plan.Counts);
        Assert.Equal([["legacy/A/A.csproj", "legacy/B/B.csproj"]], plan.Cycles);
    }

    [Fact]
    public void Entries_carry_blast_radius_blockers_and_cycle_membership()
    {
        var byId = MigrationPlanner.Plan(Model, null, false, []).Order.ToDictionary(e => e.Project);

        Assert.Equal(6, byId["lib/Contracts/Contracts.csproj"].BlastRadius);
        Assert.Equal(4, byId["src/Core/Core.csproj"].BlastRadius);
        Assert.Equal(["src/Core/Core.csproj", "src/Data/Data.csproj"], byId["src/Web/Web.csproj"].Blockers);
        Assert.Equal(ProjectReadiness.Blocked, byId["legacy/A/A.csproj"].Readiness);
        Assert.True(byId["legacy/B/B.csproj"].InCycle);
        Assert.False(byId["src/Report/Report.csproj"].InCycle);
    }

    [Fact]
    public void For_lists_only_the_framework_only_closure_in_order()
    {
        var plan = MigrationPlanner.Plan(Model, "src/Web/Web.csproj", false, []);

        Assert.Equal(["src/Core/Core.csproj", "src/Data/Data.csproj", "src/Web/Web.csproj"], plan.Order.Select(e => e.Project));
        Assert.Empty(plan.Cycles);
        Assert.Equal("src/Web/Web.csproj", plan.For);
        Assert.Empty(MigrationPlanner.Plan(Model, "src/Api/Api.csproj", false, []).Order);
    }

    [Fact]
    public void Frontier_and_kind_filters_narrow_the_listing_but_not_the_numbers()
    {
        var frontier = MigrationPlanner.Plan(Model, null, true, []);
        var noTests = MigrationPlanner.Plan(Model, null, false, [ProjectKind.Test]);

        Assert.Equal(["src/Core/Core.csproj"], frontier.Order.Select(e => e.Project));
        Assert.Equal(4, frontier.Order[0].BlastRadius);
        Assert.DoesNotContain(noTests.Order, e => e.Kind == ProjectKind.Test);
        Assert.Equal(4, noTests.Order.Single(e => e.Project == "src/Core/Core.csproj").BlastRadius);
    }

    [Theory]
    [InlineData("dual-target")]
    [InlineData("netfx-only")]
    [InlineData("cycle")]
    [InlineData("versions")]
    [InlineData("windows-only-build-steps")]
    public void Every_project_comes_after_the_framework_only_projects_it_needs(string fixture)
    {
        AssertWavesAreValid(FixtureModels.Load(fixture));
    }

    [Fact]
    public void Waves_are_valid_on_the_synthetic_model()
    {
        AssertWavesAreValid(Model);
    }

    [Fact]
    public void A_check_that_ignores_dependencies_would_be_caught()
    {
        // The validity check itself must be able to fail: a plan with Web before Data is invalid.
        var plan = MigrationPlanner.Plan(Model, null, false, []);
        var broken = plan.Order.Select(e => e.Project == "src/Web/Web.csproj" ? e with { Wave = 1 } : e).ToList();

        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertOrder(Model, broken));
    }

    private static void AssertWavesAreValid(WorkspaceModel model) =>
        AssertOrder(model, MigrationPlanner.Plan(model, null, false, []).Order);

    private static void AssertOrder(WorkspaceModel model, IReadOnlyList<PlanEntry> order)
    {
        var wave = order.ToDictionary(e => e.Project, e => e.Wave);
        var cycleOf = model.Graph.Cycles.SelectMany((c, i) => c.Select(m => (m, i))).ToDictionary(x => x.m, x => x.i);
        var classes = model.Projects.ToDictionary(p => p.Id, p => p.FrameworkClass);
        foreach (var edge in model.Graph.Edges)
        {
            if (classes[edge.From] != FrameworkClass.Framework || classes[edge.To] != FrameworkClass.Framework)
            {
                continue;
            }

            var sameCycle = cycleOf.TryGetValue(edge.From, out var a) && cycleOf.TryGetValue(edge.To, out var b) && a == b;
            Assert.True(sameCycle ? wave[edge.From] == wave[edge.To] : wave[edge.From] > wave[edge.To],
                $"{edge.From} (wave {wave[edge.From]}) depends on {edge.To} (wave {wave[edge.To]}).");
        }
    }

    private static WorkspaceModel ModelOf(params ProjectInfo[] projects) => new()
    {
        CreatedAt = "2026-09-25T20:11:04Z",
        RepositoryRoot = "/repo",
        Solution = "App.sln",
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Projects = [.. projects.OrderBy(p => p.Id, StringComparer.Ordinal)],
        Graph = GraphBuilder.Build(projects),
    };
}
