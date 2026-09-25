using Offramp.Core.Output;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class UnifiedDiffTests
{
    [Fact]
    public void Equal_texts_produce_no_diff() =>
        Assert.Equal("", UnifiedDiff.Create("a.txt", "a.txt", "x\ny\n", "x\ny\n"));

    [Fact]
    public void New_file_diff_uses_dev_null()
    {
        var diff = UnifiedDiff.ForNewFile("offramp.yml", "a\nb\n");
        Assert.Equal("--- /dev/null\n+++ b/offramp.yml\n@@ -0,0 +1,2 @@\n+a\n+b\n", diff);
    }

    [Fact]
    public void Changes_far_apart_become_separate_hunks()
    {
        var old = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));
        var changed = old.Replace("line 2\n", "line two\n", StringComparison.Ordinal)
            .Replace("line 18\n", "", StringComparison.Ordinal);

        var diff = UnifiedDiff.Create("f.txt", "f.txt", old, changed);

        Assert.Equal(2, diff.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("@@ -1,5 +1,5 @@\n line 1\n-line 2\n+line two\n", diff, StringComparison.Ordinal);
        Assert.Contains("-line 18\n", diff, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Pairs => new()
    {
        { "a\nb\nc\n", "a\nc\n" },
        { "", "x\n" },
        { "x\n", "" },
        { "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n", "0\n1\n2\n3\nfour\n5\n6\n7\n8\n9\n10\n11\n" },
        { "same\nsame\nsame\n", "same\ndifferent\nsame\nsame\n" },
    };

    /// <summary>The check that can fail: git itself must be able to apply every diff we render.</summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Git_applies_the_diff_and_reproduces_the_new_text(string oldText, string newText)
    {
        using var dir = new ScratchDirectory("diff");
        var runner = ProcessRunner.Instance;
        if (!(await runner.RunAsync(new ProcessSpec("git", ["--version"]))).Succeeded)
        {
            Assert.Skip("git is not installed");
        }

        dir.Write("f.txt", oldText);
        var diff = oldText.Length == 0
            ? UnifiedDiff.Create(null, "f.txt", oldText, newText)
            : newText.Length == 0 ? UnifiedDiff.Create("f.txt", null, oldText, newText) : UnifiedDiff.Create("f.txt", "f.txt", oldText, newText);
        if (oldText.Length == 0)
        {
            File.Delete(dir.Combine("f.txt"));
        }

        dir.Write("change.patch", diff);
        var apply = await runner.RunAsync(new ProcessSpec("git", ["-c", "core.autocrlf=false", "apply", "--unsafe-paths", "change.patch"]) { WorkingDirectory = dir.Path });

        Assert.True(apply.Succeeded, apply.StandardError + "\n" + diff);
        if (newText.Length == 0)
        {
            Assert.False(dir.Exists("f.txt"));
        }
        else
        {
            Assert.Equal(newText, dir.Read("f.txt"));
        }
    }
}
