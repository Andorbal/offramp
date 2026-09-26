using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#new-code: lines that differ from the merge base of HEAD and the configured ref.</summary>
public sealed class NewCodeTests : IDisposable
{
    private readonly ScratchDirectory _repository = new("ide-new-code");
    private readonly GitService _git = new(ProcessRunner.Instance);

    public void Dispose() => _repository.Dispose();

    [Fact]
    public async Task Everything_on_a_branch_is_new_until_merged()
    {
        await InitAsync();
        await CommitAsync("A.cs", "one\ntwo\nthree\n");
        await GitAsync("checkout", "-q", "-b", "feature");
        await CommitAsync("A.cs", "one\nTWO\nthree\n");
        var head = await NewCode.CreateAsync(_repository.Path, _git, "HEAD", null, new DiagnosticBag(), CancellationToken.None);
        var branch = await NewCode.CreateAsync(_repository.Path, _git, "main", null, new DiagnosticBag(), CancellationToken.None);

        Assert.Empty(await head.NewLinesAsync("A.cs", "one\nTWO\nthree\n", CancellationToken.None));
        Assert.Equal([2], await branch.NewLinesAsync("A.cs", "one\nTWO\nthree\n", CancellationToken.None));
        Assert.Equal([2, 4], await branch.NewLinesAsync("A.cs", "one\nTWO\nthree\nfour\n", CancellationToken.None));
        Assert.Equal([2], await branch.NewLinesAsync("A.cs", "one\r\nTWO\r\nthree\r\n", CancellationToken.None));
        Assert.Equal("main", branch.Base.Ref);
        Assert.Equal(await _git.ResolveCommitAsync(_repository.Path, "main"), branch.Base.Commit);
    }

    [Fact]
    public async Task New_and_renamed_files_are_compared_with_what_they_were()
    {
        await InitAsync();
        await CommitAsync("A.cs", "one\ntwo\n");
        _repository.Write("B.cs", "fresh\n");
        await GitAsync("mv", "A.cs", "Renamed.cs");
        _repository.Write("Renamed.cs", "one\ntwo\nthree\n");
        var newCode = await NewCode.CreateAsync(_repository.Path, _git, null, null, new DiagnosticBag(), CancellationToken.None);

        Assert.Equal(["B.cs", "Renamed.cs"], await newCode.ChangedFilesAsync(CancellationToken.None));
        Assert.Equal([1], await newCode.NewLinesAsync("B.cs", "fresh\n", CancellationToken.None));
        Assert.Equal([3], await newCode.NewLinesAsync("Renamed.cs", "one\ntwo\nthree\n", CancellationToken.None));
        Assert.Equal("HEAD", newCode.Base.Ref);
    }

    [Fact]
    public async Task Files_and_all_widen_the_scope()
    {
        await InitAsync();
        await CommitAsync("A.cs", "one\ntwo\nthree\n");
        var files = await NewCode.CreateAsync(_repository.Path, _git, null, NewCode.Files, new DiagnosticBag(), CancellationToken.None);
        var all = await NewCode.CreateAsync(_repository.Path, _git, null, NewCode.All, new DiagnosticBag(), CancellationToken.None);

        Assert.Equal([1, 2, 3], await files.NewLinesAsync("A.cs", "one\n2\nthree\n", CancellationToken.None));
        Assert.Empty(await files.NewLinesAsync("A.cs", "one\ntwo\nthree\n", CancellationToken.None));
        Assert.Equal([1, 2, 3], await all.NewLinesAsync("A.cs", "one\ntwo\nthree\n", CancellationToken.None));
    }

    [Fact]
    [ProducesDiagnostic("OFR6007")]
    public async Task Without_git_everything_is_new_and_an_unknown_ref_falls_back_to_head()
    {
        var outside = new DiagnosticBag();
        var none = await NewCode.CreateAsync(_repository.Path, _git, null, null, outside, CancellationToken.None);
        await InitAsync();
        await CommitAsync("A.cs", "one\n");
        var unknown = new DiagnosticBag();
        var fallback = await NewCode.CreateAsync(_repository.Path, _git, "origin/nope", null, unknown, CancellationToken.None);

        Assert.Null(none.Base.Commit);
        Assert.Equal([1, 2], await none.NewLinesAsync("A.cs", "one\ntwo\n", CancellationToken.None));
        Assert.Equal("OFR6007", Assert.Single(outside.ToSortedList()).Code);
        Assert.Equal("HEAD", fallback.Base.Ref);
        Assert.Contains("'origin/nope' does not name a commit", Assert.Single(unknown.ToSortedList()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ranges_join_consecutive_lines()
    {
        Assert.Equal([[1, 3], [5, 5], [7, 8]], NewCode.Ranges([1, 2, 3, 5, 7, 8]));
        Assert.Empty(NewCode.Ranges([]));
    }

    private async Task InitAsync()
    {
        await GitAsync("init", "-q", "-b", "main");
        await GitAsync("config", "user.email", "tests@offramp.test");
        await GitAsync("config", "user.name", "Offramp Tests");
        await GitAsync("config", "core.autocrlf", "false");
    }

    private async Task CommitAsync(string file, string text)
    {
        _repository.Write(file, text);
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", file);
    }

    private async Task GitAsync(params string[] arguments)
    {
        var result = await ProcessRunner.Instance.RunAsync(new ProcessSpec("git", arguments) { WorkingDirectory = _repository.Path });
        Assert.True(result.Succeeded, result.StandardError);
    }
}
