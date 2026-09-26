using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#counterparts-and-the-project-map.</summary>
public sealed class CounterpartTests
{
    [Fact]
    public async Task Without_a_map_the_portable_projects_already_referenced_are_the_counterparts()
    {
        var model = WorkspaceStore.Read((await ScannedFixtures.GetAsync(Engines.Fixture)).WorkspacePath);

        var counterparts = Counterparts.Resolve(model, new OfframpConfig(), new IdeSettings(), new DiagnosticBag());
        var none = Counterparts.Resolve(model, new OfframpConfig { Ide = new IdeConfig { ImplicitCounterparts = false } }, new IdeSettings(), new DiagnosticBag());

        Assert.Equal(
            [(Engines.Foo, "referenced", $"{Engines.ModernF},{Engines.Shared}"), (Engines.Legacy, "none", "")],
            counterparts.Select(c => (c.Project, c.Source, string.Join(",", c.Counterparts))));
        Assert.All(none, c => Assert.Empty(c.Counterparts));
    }

    [Fact]
    public async Task Map_entries_come_from_the_editor_then_offramp_yml_and_conventions_skip_missing_projects()
    {
        var model = WorkspaceStore.Read((await ScannedFixtures.GetAsync(Engines.Fixture)).WorkspacePath);
        var config = new OfframpConfig
        {
            ProjectMap =
            [
                new ProjectMapEntry { From = "src/Foo/Foo.csproj", To = "Shared" },
                new ProjectMapEntry { From = "*", To = "{name}.Portable" },
            ],
        };
        var diagnostics = new DiagnosticBag();

        var counterparts = Counterparts.Resolve(model, config, Engines.LegacyMapped with
        {
            ProjectMap = [.. Engines.LegacyMapped.ProjectMap, new ProjectMapEntry { From = "Foo", To = "src/ModernF/ModernF.csproj" }],
        }, diagnostics);

        Assert.Equal(
            [(Engines.Foo, "map", $"{Engines.ModernF},{Engines.Shared}"), (Engines.Legacy, "map", Engines.ModernF)],
            counterparts.Select(c => (c.Project, c.Source, string.Join(",", c.Counterparts))));
        Assert.Equal(0, diagnostics.Count);
    }

    [Fact]
    [ProducesDiagnostic("OFR6002")]
    [ProducesDiagnostic("OFR6004")]
    public async Task Entries_that_name_no_project_or_a_project_that_cannot_take_the_code_are_dropped()
    {
        var model = WorkspaceStore.Read((await ScannedFixtures.GetAsync(Engines.Fixture)).WorkspacePath);
        var diagnostics = new DiagnosticBag();

        var counterparts = Counterparts.Resolve(model, new OfframpConfig(), new IdeSettings
        {
            ProjectMap =
            [
                new ProjectMapEntry { From = "Foo", To = "Nope" },
                new ProjectMapEntry { From = "Foo", To = "Legacy" },
                new ProjectMapEntry { From = "Legacy", To = "Legacy" },
            ],
        }, diagnostics);

        Assert.All(counterparts, c => Assert.Equal(("map", 0), (c.Source, c.Counterparts.Count)));
        Assert.Equal(
            [
                ("OFR6002", "projectMap entry Foo → Nope (the editor's offramp.projectMap): 'Nope' is not a project of the workspace model."),
                ("OFR6002", "projectMap entry Legacy → Legacy (the editor's offramp.projectMap) maps src/Legacy/Legacy.csproj to itself."),
                ("OFR6004", "src/Legacy/Legacy.csproj cannot take code from src/Foo/Foo.csproj: it is .NET Framework-only."),
            ],
            diagnostics.ToSortedList().Select(d => (d.Code, d.Message)).Order());
    }

    [Fact]
    public async Task A_counterpart_that_depends_on_the_project_or_is_frozen_cannot_take_its_code()
    {
        var model = WorkspaceStore.Read((await ScannedFixtures.GetAsync(Engines.Fixture)).WorkspacePath);
        var foo = model.Projects.Single(p => p.Id == Engines.Foo);
        var modernF = model.Projects.Single(p => p.Id == Engines.ModernF);
        var above = model with { Graph = model.Graph with { Edges = [.. model.Graph.Edges, new Offramp.Core.Model.GraphEdge(Engines.ModernF, Engines.Foo, Offramp.Core.Model.GraphEdgeKind.Project)] } };
        var frozen = new OfframpConfig { Projects = [new ProjectOverride { Path = Engines.ModernF, Frozen = true }] };

        Assert.Null(Counterparts.Problem(model, new OfframpConfig(), foo, modernF));
        Assert.Equal("it depends on src/Foo/Foo.csproj", Counterparts.Problem(above, new OfframpConfig(), foo, modernF));
        Assert.Equal("it is frozen in offramp.yml", Counterparts.Problem(model, frozen, foo, modernF));
    }
}
