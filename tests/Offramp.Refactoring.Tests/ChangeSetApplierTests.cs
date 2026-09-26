using System.Text;
using Offramp.Core.Caching;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Refactoring.ChangeSets;

namespace Offramp.Refactoring.Tests;

public sealed class ChangeSetApplierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Renames_are_staged_pure_moves_and_everything_else_is_left_unstaged()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        var changeSet = SampleChangeSet(repository.Path);
        var applier = new ChangeSetApplier(repository.Path, new GitService(ProcessRunner.Instance));

        var journal = await applier.ApplyAsync(changeSet, "move tests", Now, TestContext.Current.CancellationToken);

        Assert.Equal(".offramp/journal/20260926-040000-move-tests.json", journal);
        Assert.Equal(JournalState.Applied, applier.Read(journal).State);
        var staged = (await repository.GitAsync("diff", "--cached", "-M100%", "--name-status")).StandardOutput.Trim();
        Assert.Equal("R100\tsrc/Legacy.Core/Thumbnails.cs\tsrc/Moved/Deep/Thumbnails.cs", staged);
        var status = (await repository.GitAsync("status", "--porcelain", "--untracked-files=all", "--", "src")).StandardOutput;
        Assert.Contains(" M src/Legacy.App/Legacy.App.csproj", status, StringComparison.Ordinal);
        Assert.Contains("?? src/New/New.csproj", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_restores_the_tree_exactly()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        var before = Snapshot(repository.Path);
        var statusBefore = (await repository.GitAsync("status", "--porcelain", "--untracked-files=all", "--", "src")).StandardOutput;
        var applier = new ChangeSetApplier(repository.Path, new GitService(ProcessRunner.Instance));
        var journal = await applier.ApplyAsync(SampleChangeSet(repository.Path), "move tests", Now, TestContext.Current.CancellationToken);

        await applier.RollbackAsync(journal, TestContext.Current.CancellationToken);

        Assert.Equal(before, Snapshot(repository.Path));
        Assert.Equal(statusBefore, (await repository.GitAsync("status", "--porcelain", "--untracked-files=all", "--", "src")).StandardOutput);
        Assert.False(Directory.Exists(repository.Directory.Combine("src", "Moved")));
        Assert.False(Directory.Exists(repository.Directory.Combine("src", "New")));
        Assert.Equal(JournalState.RolledBack, applier.Read(journal).State);
    }

    [Fact]
    public async Task A_deleted_file_comes_back_on_rollback_and_blocks_it_when_recreated()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        repository.Directory.Write("src/Legacy.App/packages.config", "<packages />\n");
        var changeSet = new ChangeSet();
        changeSet.Delete(repository.Path, "src/Legacy.App/packages.config");
        var applier = new ChangeSetApplier(repository.Path, new GitService(ProcessRunner.Instance));

        var journal = await applier.ApplyAsync(changeSet, "csproj modernize", Now, TestContext.Current.CancellationToken);

        Assert.False(repository.Directory.Exists("src/Legacy.App/packages.config"));
        Assert.Contains("deleted file src/Legacy.App/packages.config", changeSet.Preview(), StringComparison.Ordinal);
        await applier.RollbackAsync(journal, TestContext.Current.CancellationToken);
        Assert.Equal("<packages />\n", repository.Directory.Read("src/Legacy.App/packages.config"));

        var again = await applier.ApplyAsync(changeSet, "csproj modernize", Now, TestContext.Current.CancellationToken);
        repository.Directory.Write("src/Legacy.App/packages.config", "<packages>recreated</packages>\n");
        var conflict = await Assert.ThrowsAsync<RollbackConflictException>(() => applier.RollbackAsync(again, TestContext.Current.CancellationToken));
        Assert.Equal(["src/Legacy.App/packages.config"], conflict.Paths);
    }

    [Fact]
    public async Task Rollback_refuses_to_discard_later_changes()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        var applier = new ChangeSetApplier(repository.Path, new GitService(ProcessRunner.Instance));
        var journal = await applier.ApplyAsync(SampleChangeSet(repository.Path), "move tests", Now, TestContext.Current.CancellationToken);
        File.AppendAllText(repository.Directory.Combine("src", "Legacy.App", "Legacy.App.csproj"), "<!-- later -->\n");

        var conflict = await Assert.ThrowsAsync<RollbackConflictException>(() => applier.RollbackAsync(journal, TestContext.Current.CancellationToken));

        Assert.Equal(["src/Legacy.App/Legacy.App.csproj"], conflict.Paths);
        Assert.True(File.Exists(repository.Directory.Combine("src", "Moved", "Deep", "Thumbnails.cs")));
        Assert.Equal(JournalState.Applied, applier.Read(journal).State);
    }

    [Fact]
    public async Task A_file_changed_since_planning_is_never_moved()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        var changeSet = SampleChangeSet(repository.Path);
        File.AppendAllText(repository.Directory.Combine("src", "Legacy.Core", "Thumbnails.cs"), "// edited\n");
        var applier = new ChangeSetApplier(repository.Path, new GitService(ProcessRunner.Instance));

        var conflict = await Assert.ThrowsAsync<JournalConflictException>(() => applier.ApplyAsync(changeSet, "move tests", Now, TestContext.Current.CancellationToken));

        Assert.Equal(["src/Legacy.Core/Thumbnails.cs"], conflict.Paths);
        Assert.True(File.Exists(repository.Directory.Combine("src", "Legacy.Core", "Thumbnails.cs")));
    }

    [Fact]
    public void Preview_is_a_unified_diff_then_the_renames()
    {
        var changeSet = new ChangeSet();
        changeSet.Edit("a.csproj", Utf8("<Project>\n  <A />\n</Project>\n"), Utf8("<Project>\n  <A />\n  <B />\n</Project>\n"));
        changeSet.Create("new.txt", "hello\n");
        changeSet.Renames.Add(new FileRename("x/One.cs", "y/One.cs", "0"));

        Assert.Equal(
            "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,1 @@\n+hello\n" +
            "--- a/a.csproj\n+++ b/a.csproj\n@@ -1,3 +1,4 @@\n <Project>\n   <A />\n+  <B />\n </Project>\n" +
            "rename from x/One.cs\nrename to y/One.cs\n",
            changeSet.Preview());
    }

    [Fact]
    public void An_edit_that_changes_nothing_is_dropped()
    {
        var changeSet = new ChangeSet();
        changeSet.Edit("a.csproj", Utf8("x"), Utf8("x"));

        Assert.True(changeSet.IsEmpty);
    }

    private static ChangeSet SampleChangeSet(string root)
    {
        var changeSet = new ChangeSet();
        changeSet.Rename(root, "src/Legacy.Core/Thumbnails.cs", "src/Moved/Deep/Thumbnails.cs");
        var project = File.ReadAllBytes(Path.Combine(root, "src", "Legacy.App", "Legacy.App.csproj"));
        changeSet.Edit("src/Legacy.App/Legacy.App.csproj", project, [.. project, .. Utf8("<!-- edited -->\n")]);
        changeSet.Create("src/New/New.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        return changeSet;
    }

    /// <summary>Every file under src/ with its hash.</summary>
    private static List<string> Snapshot(string root) =>
        [.. Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/') + " " + ContentHash.Sha256File(f))
            .Order(StringComparer.Ordinal)];

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
