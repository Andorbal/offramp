using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class GitServiceTests
{
    [Fact]
    public void Porcelain_z_output_is_parsed_including_renames()
    {
        var output = "R  new/B.cs\0old/B.cs\0 M src/Foo.csproj\0?? notes.txt\0";

        var entries = GitService.ParsePorcelainZ(output);

        Assert.Equal(
        [
            new GitStatusEntry('R', ' ', "new/B.cs", "old/B.cs"),
            new GitStatusEntry('?', '?', "notes.txt", null),
            new GitStatusEntry(' ', 'M', "src/Foo.csproj", null),
        ], entries);
    }

    [Fact]
    public async Task Version_is_null_when_git_is_missing()
    {
        var git = new GitService(new FakeProcessRunner());
        Assert.Null(await git.GetVersionAsync());
        Assert.Null(await git.FindRepositoryRootAsync("/anywhere"));
    }

    [Fact]
    public async Task Moves_inside_a_repository_are_staged_renames_with_identical_bytes()
    {
        using var repo = new ScratchDirectory("git");
        var git = new GitService(ProcessRunner.Instance);
        if (await git.GetVersionAsync() is null)
        {
            Assert.Skip("git is not installed");
        }

        await Git(repo, "init", "-q");
        await Git(repo, "config", "user.email", "test@example.com");
        await Git(repo, "config", "user.name", "Test");
        await Git(repo, "config", "core.autocrlf", "false");
        var bytes = "﻿namespace A;\r\npublic class B { }\n"u8.ToArray();
        Directory.CreateDirectory(repo.Combine("old"));
        await File.WriteAllBytesAsync(repo.Combine("old", "B.cs"), bytes);
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "init");

        await git.MoveAsync(repo.Path, repo.Combine("old", "B.cs"), repo.Combine("new", "deeper", "B.cs"));

        Assert.Equal(await git.FindRepositoryRootAsync(repo.Path), repo.Path);
        var status = await git.StatusAsync(repo.Path);
        var entry = Assert.Single(status);
        Assert.Equal('R', entry.Index);
        Assert.Equal("new/deeper/B.cs", entry.Path);
        Assert.Equal("old/B.cs", entry.OriginalPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(repo.Combine("new", "deeper", "B.cs")));
    }

    [Fact]
    public async Task Moves_outside_a_repository_are_plain_file_moves()
    {
        using var dir = new ScratchDirectory("nogit");
        var git = new GitService(new FakeMachine { RepositoryRoot = null }.CreateRunner());
        dir.Write("a.txt", "x");

        await git.MoveAsync(dir.Path, dir.Combine("a.txt"), dir.Combine("sub", "a.txt"));

        Assert.False(dir.Exists("a.txt"));
        Assert.Equal("x", dir.Read("sub/a.txt"));
    }

    private static async Task Git(ScratchDirectory repo, params string[] args)
    {
        var result = await ProcessRunner.Instance.RunAsync(new ProcessSpec("git", args) { WorkingDirectory = repo.Path });
        Assert.True(result.Succeeded, result.StandardError);
    }
}
