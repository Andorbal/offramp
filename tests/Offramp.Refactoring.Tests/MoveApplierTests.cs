using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Moves;

namespace Offramp.Refactoring.Tests;

/// <summary><c>move apply</c> without builds: batches, verification policies (a shell command), rollback, keep, and resume.</summary>
public sealed class MoveApplierTests
{
    private const string PlanPath = "move-plan.json";

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 4, 0, 0, TimeSpan.Zero);

    private static readonly string[] Planned = ["src/Legacy/Clean/Money.cs", "src/Legacy/Orders/OrderMapper.cs"];

    [Fact]
    public void Batches_keep_files_that_need_each_other_together_and_needed_files_first()
    {
        var moves = new[] { Move("a.cs", "b.cs"), Move("b.cs"), Move("c.cs", "d.cs"), Move("d.cs", "c.cs"), Move("e.cs") };

        var batches = MoveApplier.Batches(moves, 2);

        Assert.Equal([["b.cs", "a.cs"], ["c.cs", "d.cs"], ["e.cs"]], batches.Select(b => b.Select(m => m.File).ToArray()));
        Assert.Single(MoveApplier.Batches(moves, null));
        Assert.Equal([["b.cs"], ["a.cs"], ["c.cs", "d.cs"], ["e.cs"]], MoveApplier.Batches(moves, 1).Select(b => b.Select(m => m.File).ToArray()));
    }

    [Fact]
    public async Task Batch_verification_runs_after_each_batch()
    {
        using var repository = await FixtureRepository.CreateAsync("move-cases");

        var outcome = await ApplyAsync(repository, "batch:1", "exit 0");

        Assert.Equal(MoveApplyStatus.Applied, outcome.Status);
        Assert.Equal(2, outcome.Result.Verifications.Count);
        Assert.All(outcome.Result.Verifications, v => Assert.True(v.Passed));
        Assert.Equal(Planned, outcome.Result.Moved);
        Assert.Equal(
            "R100\tsrc/Legacy/Clean/Money.cs\tsrc/Core/Clean/Money.cs\nR100\tsrc/Legacy/Orders/OrderMapper.cs\tsrc/Core/Orders/OrderMapper.cs",
            (await repository.GitAsync("diff", "--cached", "-M100%", "--name-status")).StandardOutput.Trim());
    }

    [Fact]
    [ProducesDiagnostic("OFR2050")]
    public async Task A_failed_batch_rolls_the_whole_run_back()
    {
        using var repository = await FixtureRepository.CreateAsync("move-cases");
        var before = Hashes(repository.Path);
        var diagnostics = new DiagnosticBag();

        var outcome = await ApplyAsync(repository, "batch:1", "exit 1", diagnostics: diagnostics);

        Assert.Equal(MoveApplyStatus.Failed, outcome.Status);
        Assert.True(outcome.Result.RolledBack);
        Assert.Single(outcome.Result.Verifications);
        Assert.True(diagnostics.Contains("OFR2050"));
        Assert.Equal(before, Hashes(repository.Path));
        Assert.Empty((await repository.GitAsync("diff", "--cached", "--name-status")).StandardOutput.Trim());
    }

    [Fact]
    [ProducesDiagnostic("OFR2152")]
    public async Task Keep_stops_at_the_failed_batch_and_resume_finishes_the_run()
    {
        using var repository = await FixtureRepository.CreateAsync("move-cases");
        var nothing = new DiagnosticBag();
        var none = await ApplyAsync(repository, "none", null, resume: true, diagnostics: nothing);
        Assert.Equal(MoveApplyStatus.NothingToResume, none.Status);
        Assert.True(nothing.Contains("OFR2152"));

        var kept = await ApplyAsync(repository, "batch:1", "exit 1", onFailure: "keep");

        Assert.Equal(MoveApplyStatus.Partial, kept.Status);
        Assert.Equal(["src/Legacy/Clean/Money.cs"], kept.Result.Moved);
        Assert.True(File.Exists(Path.Combine(repository.Path, "src", "Core", "Clean", "Money.cs")));
        Assert.True(File.Exists(Path.Combine(repository.Path, "src", "Legacy", "Orders", "OrderMapper.cs")));

        var resumed = await ApplyAsync(repository, "end", "exit 0", resume: true);

        Assert.Equal(MoveApplyStatus.Applied, resumed.Status);
        Assert.True(resumed.Result.Resumed);
        Assert.Equal(kept.Result.Journal, resumed.Result.Journal);
        Assert.Equal(Planned, resumed.Result.Moved);
        Assert.Single(resumed.Result.Verifications);
        var applier = new ChangeSetApplier(repository.Path, new GitService(ProcessRunner.Instance));
        Assert.Equal(JournalState.Applied, applier.Read(resumed.Result.Journal!).State);
        Assert.True(File.Exists(Path.Combine(repository.Path, "src", "Core", "Orders", "OrderMapper.cs")));
    }

    [Fact]
    public async Task Resume_recognizes_a_step_performed_before_the_interruption_and_refuses_a_changed_file()
    {
        using var repository = await FixtureRepository.CreateAsync("move-cases");
        var git = new GitService(ProcessRunner.Instance);
        var applier = new ChangeSetApplier(repository.Path, git);
        var changeSet = MoveChangeSet.Build(repository.Path, Plan(repository.Path), new HashSet<string>(StringComparer.Ordinal), out _, FixtureModels.Load("move-cases"));
        var journal = applier.Begin(changeSet, MoveApplier.Command, Now, PlanPath);
        await applier.ContinueAsync(journal, 2, TestContext.Current.CancellationToken);

        // Interrupted after moving Money.cs but before the journal recorded it.
        var steps = applier.Read(journal).Steps.ToList();
        var money = steps.FindIndex(s => s.From == "src/Legacy/Clean/Money.cs");
        await git.MoveAsync(repository.Path, Path.Combine(repository.Path, "src", "Legacy", "Clean", "Money.cs"), Path.Combine(repository.Path, "src", "Core", "Clean", "Money.cs"), TestContext.Current.CancellationToken);
        Assert.False(steps[money].Done);

        File.AppendAllText(Path.Combine(repository.Path, "src", "Legacy", "Orders", "OrderMapper.cs"), "// edited\n");
        var diagnostics = new DiagnosticBag();
        var refused = await ApplyAsync(repository, "none", null, resume: true, diagnostics: diagnostics);

        Assert.Equal(MoveApplyStatus.Failed, refused.Status);
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR2152" && d.Message.Contains("src/Legacy/Orders/OrderMapper.cs", StringComparison.Ordinal));
        Assert.True(applier.Read(journal).Steps[money].Done);
        Assert.Equal(JournalState.Applying, applier.Read(journal).State);
    }

    private static PlannedMove Move(string file, params string[] needs) =>
        new() { File = file, To = "dest/" + file, Sha256 = new string('0', 64), Needs = needs };

    private static MovePlanDocument Plan(string root) => new()
    {
        From = "src/Legacy/Legacy.csproj",
        To = "src/Core/Core.csproj",
        Target = "net10.0",
        WorkspaceHash = "sha256:" + new string('1', 64),
        Moves =
        [
            new PlannedMove { File = Planned[0], To = "src/Core/Clean/Money.cs", Sha256 = Hash(root, Planned[0]) },
            new PlannedMove { File = Planned[1], To = "src/Core/Orders/OrderMapper.cs", CoMoveOf = null, Sha256 = Hash(root, Planned[1]), Needs = [Planned[0]] },
        ],
        ProjectEdits =
        [
            new ProjectEdit { Project = "src/Core/Core.csproj", Kind = ProjectEditKind.AddProjectReference, Value = "src/Contracts/Contracts.csproj" },
            new ProjectEdit { Project = "src/Legacy/Legacy.csproj", Kind = ProjectEditKind.AddProjectReference, Value = "src/Core/Core.csproj" },
        ],
        Excluded = [],
        Cycles = [],
        Verify = "end",
    };

    private static Task<MoveApplyOutcome> ApplyAsync(
        FixtureRepository repository, string policy, string? command, string onFailure = "rollback", bool resume = false, DiagnosticBag? diagnostics = null)
    {
        var shell = OperatingSystem.IsWindows() ? command?.Replace("exit ", "exit /b ", StringComparison.Ordinal) : command;
        var plan = Plan(repository.Path);
        return MoveApplier.ApplyAsync(new MoveApplyRequest
        {
            RepositoryRoot = repository.Path,
            Model = FixtureModels.Load("move-cases"),
            Config = new OfframpConfig { Verify = new VerifyConfig { Mode = "command", Command = shell } },
            Plan = plan,
            PlanPath = PlanPath,
            WorkspaceHash = plan.WorkspaceHash,
            VerifyPolicy = policy,
            OnFailure = onFailure,
            Resume = resume,
            Git = new GitService(ProcessRunner.Instance),
            Processes = ProcessRunner.Instance,
            Diagnostics = diagnostics ?? new DiagnosticBag(),
            Now = Now,
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The file's hash; after it moved (a resumed run reads the journal, not the plan), a placeholder.</summary>
    private static string Hash(string root, string file) =>
        File.Exists(Path.Combine(root, file)) ? ContentHash.Sha256File(Path.Combine(root, file)) : new string('0', 64);

    private static List<string> Hashes(string root) =>
        [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/') + " " + ContentHash.Sha256File(f))
            .Order(StringComparer.Ordinal)];
}
