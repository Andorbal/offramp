using NuGet.Frameworks;
using Offramp.Fixtures.Feeds;
using Offramp.NuGet.Inspection;

namespace Offramp.NuGet.Tests;

public sealed class InspectionTests
{
    private static PackageInspection Inspect(string id, string version) =>
        PackageInspector.Inspect(FeedMaterializer.Nupkg(VersionsFeed.Load().Packages.Single(p => p.Id == id && p.Version == version)));

    [Theory]
    [InlineData("Newtonsoft.Json", "13.0.3", "net10.0", true)]
    [InlineData("Newtonsoft.Json", "8.0.3", "net10.0", false)]
    [InlineData("Newtonsoft.Json", "9.0.1", "net10.0", true)]
    [InlineData("Newtonsoft.Json", "3.5.8", "net48", true)]
    [InlineData("EntityFramework", "6.2.0", "net10.0", false)]
    [InlineData("EntityFramework", "6.4.4", "net10.0", true)]
    [InlineData("EntityFramework", "6.4.4", "net8.0", true)]
    [InlineData("Microsoft.AspNet.WebApi.Core", "5.2.9", "net10.0", false)]
    [InlineData("Microsoft.AspNet.WebApi.Core", "5.2.9", "net48", true)]
    [InlineData("Contoso.Legacy.Reports", "1.0.0", "net10.0", false)]
    public void Support_follows_nuget_compatibility(string id, string version, string target, bool expected) =>
        Assert.Equal(expected, TargetSupport.Supports(Inspect(id, version), NuGetFramework.Parse(target)));

    [Fact]
    public void Frameworks_come_from_every_asset_folder()
    {
        var inspection = Inspect("Microsoft.Extensions.Logging.Abstractions", "8.0.0");

        Assert.Contains("net8.0", inspection.AssetFrameworks);
        Assert.Contains("netstandard2.0", inspection.AssetFrameworks);
        Assert.Contains("net462", inspection.AssetFrameworks);
    }

    [Fact]
    public void Windows_only_evidence_is_for_the_assets_nuget_would_pick()
    {
        var drawing = Inspect("System.Drawing.Common", "8.0.0");

        Assert.Contains("windows6.1", TargetSupport.WindowsOnly(drawing, NuGetFramework.Parse("net10.0")), StringComparison.Ordinal);
        // .NET Standard consumers get the netstandard2.0 build, which carries no attribute.
        Assert.Null(TargetSupport.WindowsOnly(drawing, NuGetFramework.Parse("netstandard2.0")));
        Assert.Null(TargetSupport.WindowsOnly(Inspect("Newtonsoft.Json", "13.0.3"), NuGetFramework.Parse("net10.0")));
    }

    [Theory]
    [InlineData("lib/net45/A.dll", "net45")]
    [InlineData("lib/A.dll", "net11")]
    [InlineData("ref/netstandard2.0/A.dll", "netstandard2.0")]
    [InlineData("runtimes/win/lib/net8.0/A.dll", "net8.0")]
    [InlineData("contentFiles/cs/net6.0/X.cs", "net6.0")]
    [InlineData("buildTransitive/net462/A.targets", "net462")]
    [InlineData("build/A.targets", null)]
    [InlineData("tools/A.ps1", null)]
    [InlineData("lib/nonsense-framework/A.dll", null)]
    public void Asset_frameworks_are_read_from_folders(string path, string? expected) =>
        Assert.Equal(expected, PackageInspector.AssetFramework(path)?.GetShortFolderName());

    [Fact]
    public void A_package_without_assets_or_groups_supports_everything() =>
        Assert.True(TargetSupport.Supports(
            new PackageInspection { Id = "Tools", Version = "1.0.0", AssetFrameworks = [], DependencyFrameworks = [], Assemblies = [] },
            NuGetFramework.Parse("net10.0")));

    [Fact]
    public void A_meta_package_uses_its_dependency_groups() =>
        Assert.False(TargetSupport.Supports(
            new PackageInspection { Id = "Meta", Version = "1.0.0", AssetFrameworks = [], DependencyFrameworks = ["net45"], Assemblies = [] },
            NuGetFramework.Parse("net10.0")));
}
