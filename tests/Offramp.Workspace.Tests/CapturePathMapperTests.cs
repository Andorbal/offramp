using Offramp.Fixtures;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Tests;

public sealed class CapturePathMapperTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("mapper");

    public CapturePathMapperTests()
    {
        _repo.Write("src/App/App.csproj", "<Project />");
        _repo.Write("src/Lib/Lib.csproj", "<Project />");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Windows_capture_paths_map_onto_the_local_repository()
    {
        var mapper = CapturePathMapper.Infer(_repo.Path, [@"D:\a\offramp\src\App\App.csproj", @"D:\a\offramp\src\Lib\Lib.csproj"]);

        Assert.Equal("D:/a/offramp", mapper.CaptureRoot);
        Assert.Equal("src/App/App.csproj", mapper.ToRelative(@"D:\a\offramp\src\App\App.csproj"));
        Assert.Equal("src/Lib/Program.cs", mapper.ToRelative(@"d:\A\Offramp\src\Lib\Program.cs"));
        Assert.Equal("src/Lib/Lib.csproj", mapper.ToRelative(@"D:\a\offramp\src\App", @"..\Lib\Lib.csproj"));
        Assert.Null(mapper.ToRelative(@"C:\Users\runner\.nuget\packages\newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll"));
        Assert.Equal(Path.Combine(_repo.Path, "src", "App", "App.csproj"), mapper.ToLocal(@"D:\a\offramp\src\App\App.csproj"));
    }

    [Fact]
    public void Another_unix_checkout_maps_by_suffix()
    {
        var mapper = CapturePathMapper.Infer(_repo.Path, ["/home/ci/work/repo/src/App/App.csproj"]);

        Assert.Equal("/home/ci/work/repo", mapper.CaptureRoot);
        Assert.Equal("src/App/obj/project.assets.json", mapper.ToRelative("/home/ci/work/repo/src/App/obj/project.assets.json"));
        Assert.Equal("", mapper.ToRelative("/home/ci/work/repo"));
        Assert.Null(mapper.ToRelative("/home/ci/work/repository/src/App/App.csproj"));
    }

    [Fact]
    public void Paths_that_match_nothing_fall_back_to_the_local_root()
    {
        var mapper = CapturePathMapper.Infer(_repo.Path, [@"C:\elsewhere\Other.csproj"]);

        Assert.Equal(_repo.Path.Replace('\\', '/').TrimEnd('/'), mapper.CaptureRoot);
        Assert.Null(mapper.ToRelative(@"C:\elsewhere\Other.csproj"));
    }

    [Fact]
    public void Dot_segments_are_collapsed_without_touching_the_disk()
    {
        var mapper = CapturePathMapper.Local(_repo.Path);
        var root = _repo.Path.Replace('\\', '/');

        Assert.Equal("src/Lib/Lib.csproj", mapper.ToRelative(root + "/src/App/../Lib/./Lib.csproj"));
        Assert.Equal("lib/Foo.dll", mapper.ToRelative(root + "/src/App", "../../lib/Foo.dll"));
    }

    [Theory]
    [InlineData(@"C:\src\a.csproj", true)]
    [InlineData("c:/src/a.csproj", true)]
    [InlineData(@"\\server\share\a.csproj", true)]
    [InlineData("/home/user/a.csproj", false)]
    [InlineData("src/a.csproj", false)]
    public void Windows_style_paths_are_recognized(string path, bool expected) =>
        Assert.Equal(expected, CapturePathMapper.IsWindowsStyle(path));
}
