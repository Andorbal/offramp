using Offramp.Core.Paths;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed class RepoPathsTests
{
    [Fact]
    public void Canonical_resolves_a_linked_directory_along_the_path()
    {
        using var scratch = new ScratchDirectory("canonical");
        var real = Directory.CreateDirectory(Path.Combine(scratch.Path, "private", "var")).FullName;
        var link = Path.Combine(scratch.Path, "var");
        Directory.CreateSymbolicLink(link, real);

        var canonical = RepoPaths.Canonical(Path.Combine(link, "folders", "scratch"));

        Assert.Equal(Path.Combine(RepoPaths.Canonical(real), "folders", "scratch"), canonical);
        Assert.DoesNotContain(link + Path.DirectorySeparatorChar, canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_leaves_a_plain_path_alone()
    {
        using var scratch = new ScratchDirectory("canonical");
        var plain = RepoPaths.Canonical(scratch.Path);

        Assert.Equal(plain, RepoPaths.Canonical(Path.Combine(plain, ".")));
        Assert.Equal(Path.Combine(plain, "missing", "file.txt"), RepoPaths.Canonical(Path.Combine(plain, "missing", "file.txt")));
    }
}
