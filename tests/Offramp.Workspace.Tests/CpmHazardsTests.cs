using Offramp.Fixtures;
using Offramp.Workspace.Cpm;

namespace Offramp.Workspace.Tests;

public sealed class CpmHazardsTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("cpm");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_repository_without_props_files_has_no_hazards()
    {
        _repo.Write("src/A/A.csproj", "<Project />");
        _repo.Write("tools/B/B.csproj", "<Project />");

        Assert.Empty(CpmHazards.Find(_repo.Path, ["src/A/A.csproj"]));
    }

    [Fact]
    [ProducesDiagnostic("OFR1301")]
    [ProducesDiagnostic("OFR1302")]
    [ProducesDiagnostic("OFR1303")]
    public void Finds_projects_outside_the_solution_shadowing_props_and_packages_config()
    {
        _repo.Write("Directory.Packages.props", "<Project />");
        _repo.Write("src/A/A.csproj", "<Project />");
        _repo.Write("src/Legacy/Legacy.csproj", "<Project />");
        _repo.Write("src/Legacy/packages.config", "<packages />");
        _repo.Write("tools/Unrelated/Unrelated.csproj", "<Project />");
        _repo.Write("tools/Unrelated/bin/Copy.csproj", "<Project />");
        _repo.Write("nested/Directory.Packages.props", "<Project />");
        _repo.Write("nested/C/C.csproj", "<Project />");
        _repo.Write("importing/Directory.Packages.props",
            "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))\" /></Project>");

        var hazards = CpmHazards.Find(_repo.Path, ["src/A/A.csproj", "src/Legacy/Legacy.csproj", "nested/C/C.csproj"]);

        Assert.Equal(
            ["OFR1301 tools/Unrelated/Unrelated.csproj", "OFR1302 nested/Directory.Packages.props", "OFR1303 src/Legacy/Legacy.csproj"],
            hazards.Select(h => $"{h.Descriptor.Code} {h.Path}"));
        Assert.Contains("Directory.Packages.props applies to it", hazards[0].Message, StringComparison.Ordinal);
    }
}
