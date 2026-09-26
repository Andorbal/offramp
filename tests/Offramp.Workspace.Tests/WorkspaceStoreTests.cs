using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Workspace.Tests;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("store");

    public void Dispose() => _repo.Dispose();

    private string State => _repo.Combine(".offramp");

    [Fact]
    public void Inputs_are_project_solution_and_directory_files_outside_skipped_folders()
    {
        _repo.Write("App.sln", "");
        _repo.Write("Directory.Build.props", "<Project />");
        _repo.Write("Directory.Packages.props", "<Project />");
        _repo.Write("src/A/A.csproj", "<Project />");
        _repo.Write("src/A/packages.config", "<packages />");
        _repo.Write("src/A/Program.cs", "class P { }");
        _repo.Write("src/A/bin/Debug/A.csproj", "<Project />");
        _repo.Write("src/A/obj/A.csproj.nuget.g.props", "<Project />");
        _repo.Write(".offramp/slice.slnf", "{}");
        _repo.Write("slices/other.slnf", "{}");
        _repo.Write(".git/config", "");

        var inputs = WorkspaceInputs.Collect(_repo.Path, State);

        Assert.Equal(
            ["App.sln", "Directory.Build.props", "Directory.Packages.props", "src/A/A.csproj", "src/A/packages.config"],
            inputs.Select(i => i.Path));
        Assert.All(inputs, i => Assert.Equal(64, i.Sha256.Length));
        Assert.Contains("slices/other.slnf", WorkspaceInputs.Collect(_repo.Path, State, "slices/other.slnf").Select(i => i.Path));
    }

    [Fact]
    public void A_model_with_the_same_inputs_is_fresh_and_any_change_makes_it_stale()
    {
        _repo.Write("src/A/A.csproj", "<Project />");
        _repo.Write("src/B/B.csproj", "<Project />");
        var model = Model(WorkspaceInputs.Collect(_repo.Path, State));

        Assert.False(WorkspaceInputs.Compare(model, _repo.Path, State).IsStale);

        // Touching a file without changing it is not a change.
        File.SetLastWriteTimeUtc(_repo.Combine("src", "A", "A.csproj"), DateTime.UtcNow.AddDays(1));
        Assert.False(WorkspaceInputs.Compare(model, _repo.Path, State).IsStale);

        _repo.Write("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.Delete(_repo.Combine("src", "B", "B.csproj"));
        _repo.Write("src/C/C.csproj", "<Project />");
        var staleness = WorkspaceInputs.Compare(model, _repo.Path, State);

        Assert.Equal(["src/A/A.csproj"], staleness.Changed);
        Assert.Equal(["src/C/C.csproj"], staleness.Added);
        Assert.Equal(["src/B/B.csproj"], staleness.Removed);
        Assert.Equal("1 changed (src/A/A.csproj); 1 added (src/C/C.csproj); 1 removed (src/B/B.csproj)", staleness.Describe());
    }

    [Fact]
    public void A_changed_source_log_makes_the_model_stale()
    {
        var log = _repo.Write("ci/msbuild.binlog", "one");
        var model = Model([]) with { Source = new WorkspaceSource(WorkspaceSourceKind.Binlog, "ci/msbuild.binlog", Core.Caching.ContentHash.Sha256File(log)) };
        Assert.False(WorkspaceInputs.Compare(model, _repo.Path, State).IsStale);

        _repo.Write("ci/msbuild.binlog", "two");

        Assert.True(WorkspaceInputs.Compare(model, _repo.Path, State).SourceChanged);
    }

    [Fact]
    public void Ledger_snapshots_are_named_by_date_and_content()
    {
        var model = Model([]) with
        {
            Projects =
            [
                new ProjectInfo { Id = "a.csproj", Name = "a", Kind = ProjectKind.Library, FrameworkClass = FrameworkClass.Framework, Loc = 10 },
                new ProjectInfo { Id = "b.csproj", Name = "b", Kind = ProjectKind.Test, FrameworkClass = FrameworkClass.Modern, Loc = 5 },
            ],
        };
        var snapshot = Ledger.Snapshot(model);

        Assert.Equal(15, snapshot.Totals.Loc);
        Assert.Equal(new ClassTotals(1, 10), snapshot.Totals.ByFrameworkClass["framework"]);
        Assert.Equal(1, snapshot.Totals.ByKind["test"]);

        var first = Ledger.Write(snapshot, _repo.Combine(".offramp", "ledger"), _repo.Path);
        var later = Ledger.Write(snapshot with { CreatedAt = "2026-09-25T23:59:59Z" }, _repo.Combine(".offramp", "ledger"), _repo.Path);
        var changed = Ledger.Write(Ledger.Snapshot(model with { Projects = model.Projects.Take(1).ToList() }), _repo.Combine(".offramp", "ledger"), _repo.Path);

        Assert.Matches("^\\.offramp/ledger/2026-09-25-[0-9a-f]{8}\\.json$", first);
        Assert.Equal(first, later);
        Assert.NotEqual(first, changed);
    }

    private WorkspaceModel Model(IReadOnlyList<InputFile> inputs) => new()
    {
        CreatedAt = "2026-09-25T20:11:04Z",
        RepositoryRoot = _repo.Path,
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Inputs = inputs,
    };
}
