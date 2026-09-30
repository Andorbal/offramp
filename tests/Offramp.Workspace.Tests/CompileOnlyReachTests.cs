using Offramp.Fixtures;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Scanning;

namespace Offramp.Workspace.Tests;

/// <summary>Why the compile-only block does not reach a legacy project (OFR0122), from its evaluation.</summary>
public sealed class CompileOnlyReachTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("reach");

    public void Dispose() => _repo.Dispose();

    [Fact]
    [ProducesDiagnostic("OFR0122")]
    public void Each_cause_is_named_with_the_file_to_change()
    {
        _repo.Write("Directory.Build.props", CompileOnlyConditional.Apply(null)!);
        var settings = _repo.Write("writer.build.settings", "<Project><PropertyGroup><MSBuildExtensionsPath>$(MSBuildToolsPath)\\MsBuildExtensions</MSBuildExtensionsPath></PropertyGroup></Project>");
        var nearer = _repo.Write("src/C/Directory.Build.props", "<Project />");
        var disabled = _repo.Write("src/B/B.csproj", "<Project><PropertyGroup><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps></PropertyGroup></Project>");
        var projects = new (string, IReadOnlyList<EvaluatedProject>)[]
        {
            // Open Live Writer: writer.build.settings overrides MSBuildExtensionsPath, so Microsoft.Common.props is never imported.
            ("src/A/A.csproj", [Evaluation(_repo.Write("src/A/A.csproj", "<Project />"), [], settings)]),
            ("src/B/B.csproj", [Evaluation(disabled, new() { ["MicrosoftCommonPropsHasBeenImported"] = "true", ["ImportDirectoryBuildProps"] = "false" })]),
            ("src/C/C.csproj", [Evaluation(_repo.Write("src/C/C.csproj", "<Project />"), new() { ["MicrosoftCommonPropsHasBeenImported"] = "true", ["DirectoryBuildPropsPath"] = nearer })]),
            ("src/D/D.csproj", [Evaluation(_repo.Write("src/D/D.csproj", "<Project />"), new() { ["OfframpCompileOnly"] = "true" })]),
            ("src/E/E.csproj", [Evaluation(_repo.Write("src/E/E.csproj", "<Project />"), new() { ["UsingMicrosoftNETSdk"] = "true" })]),
            ("src/Native/Native.vcxproj", [Evaluation(_repo.Write("src/Native/Native.vcxproj", "<Project />"), [], settings)]),
        };

        var gaps = CompileOnlyReach.Find(_repo.Path, projects, CapturePathMapper.Local(_repo.Path));

        Assert.Equal(
            [
                ("src/A/A.csproj", "msbuild-extensions-path", "writer.build.settings"),
                ("src/B/B.csproj", "import-disabled", "src/B/B.csproj"),
                ("src/C/C.csproj", "nearer-props", "src/C/Directory.Build.props"),
            ],
            gaps.Select(g => (g.Project, g.Cause, g.File)));
        Assert.StartsWith("writer.build.settings sets MSBuildExtensionsPath, so MSBuild never imports Microsoft.Common.props", gaps[0].Reason, StringComparison.Ordinal);
        Assert.EndsWith("condition that property on '$(OS)' == 'Windows_NT'", gaps[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_block_in_the_root_props_file_nothing_is_reported()
    {
        _repo.Write("Directory.Build.props", "<Project />");
        var project = _repo.Write("src/A/A.csproj", "<Project />");

        Assert.Empty(CompileOnlyReach.Find(_repo.Path, [("src/A/A.csproj", [Evaluation(project, [])])], CapturePathMapper.Local(_repo.Path)));
    }

    private static EvaluatedProject Evaluation(string projectFile, Dictionary<string, string> properties, params string[] imports) => new()
    {
        ProjectFile = projectFile,
        TargetFramework = "net461",
        Properties = properties,
        Items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>(),
        Imports = [.. imports, "/usr/share/dotnet/sdk/10.0.100/Microsoft.Common.CurrentVersion.targets"],
    };
}
