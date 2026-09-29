using Offramp.Fixtures;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Scanning;

namespace Offramp.Workspace.Tests;

/// <summary>How a failed build is summarized (OFR0130) and what OFR0101 says stopped a project.</summary>
public sealed class BuildFailuresTests
{
    private const string Root = "/repo";

    [Fact]
    [ProducesDiagnostic("OFR0130")]
    public void Errors_count_once_per_project_and_say_how_many_projects_each_code_affects()
    {
        // Open Live Writer: MSB3644 in 25 projects, reported in the shared targets file, read as "2 error(s)".
        const string targets = "/usr/share/dotnet/sdk/10.0.100/Microsoft.Common.CurrentVersion.targets";
        const string nuget = "/usr/share/dotnet/sdk/10.0.100/NuGet.targets";
        var errors = Enumerable.Range(1, 25)
            .Select(i => new BuildError("MSB3644", "The reference assemblies for .NETFramework,Version=v4.6.1 were not found.", $"{Root}/src/P{i:00}/P{i:00}.csproj", targets, 1259, 5))
            .Append(new BuildError("MSB3644", "The reference assemblies for .NETFramework,Version=v4.6.1 were not found.", $"{Root}/src/P01/P01.csproj", targets, 1259, 5))
            .Append(new BuildError("", $"Failed to download package 'System.Memory.4.5.4' from '{Root}/feed'.\nThe proxy tunnel request failed with status code '403'.", $"{Root}/src/All.sln", nuget, 196, 5))
            .Append(new BuildError("", "Failed to download package 'System.Buffers.4.5.1'.", $"{Root}/src/All.sln", nuget, 196, 5))
            .Append(new BuildError("MSB4278", "The imported file \"$(VCTargetsPath)/Microsoft.Cpp.Default.props\" does not exist.", $"{Root}/src/Native/Native.vcxproj", $"{Root}/src/Native/Native.vcxproj", 20, 3))
            .ToList();

        var (message, data) = BuildFailures.Summarize(errors, Relative, text => text.Replace(Root + "/", "", StringComparison.Ordinal));

        Assert.StartsWith("The build failed with 28 error(s) (MSB3644 ×25 in 25 projects, restore ×2, MSB4278 ×1); the model is partial. First: ", message, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Common.CurrentVersion.targets(1259): MSB3644: The reference assemblies", message, StringComparison.Ordinal);
        Assert.Contains("NuGet.targets(196): restore: Failed to download package 'System.Memory.4.5.4' from 'feed'.", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Root + "/", message, StringComparison.Ordinal);
        var values = data.ToDictionary(d => d.Key, d => d.Value!.ToJsonString());
        Assert.Equal("28", values["errorCount"]);
        Assert.Equal("{\"MSB3644\":25,\"restore\":2,\"MSB4278\":1}", values["byCode"]);
        Assert.Equal("{\"MSB3644\":25,\"restore\":1,\"MSB4278\":1}", values["projectsByCode"]);
    }

    [Fact]
    public void A_restore_failure_or_a_build_that_evaluated_nothing_explains_every_missing_project()
    {
        BuildError[] restore = [new("", "Failed to download package 'System.Memory.4.5.4'.\nThe proxy said 403.", $"{Root}/All.sln", "/sdk/NuGet.targets", 196, 5)];
        BuildError[] website = [new("MSB4249", "Unable to build website project \"Web\". The ASP.NET compiler is only available on the .NET Framework version of MSBuild.", $"{Root}/All.sln", $"{Root}/All.sln", null, null)];

        Assert.Equal("not built: the restore failed, so MSBuild built nothing (restore: Failed to download package 'System.Memory.4.5.4'.)", BuildFailures.RestoreFailed(restore, t => t));
        Assert.Null(BuildFailures.RestoreFailed(website, t => t));
        Assert.StartsWith("not built: MSBuild stopped before building any project (MSB4249: Unable to build website project", BuildFailures.NothingBuilt(website, 0, t => t), StringComparison.Ordinal);
        Assert.Null(BuildFailures.NothingBuilt(website, 3, t => t));
        Assert.Equal("NU1101", BuildFailures.Label(new BuildError("NU1101", "Unable to find package X.", null, null, null, null)));
        Assert.True(BuildFailures.IsRestoreError(new BuildError("NU1101", "Unable to find package X.", null, null, null, null)));
        Assert.Equal("no code", BuildFailures.Label(new BuildError("", "Something failed.", null, "/repo/build.targets", 1, 1)));
    }

    private static string? Relative(string path) =>
        path.StartsWith(Root + "/", StringComparison.Ordinal) ? path[(Root.Length + 1)..] : null;
}
