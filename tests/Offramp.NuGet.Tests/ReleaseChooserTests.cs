using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Fixtures.Feeds;
using Offramp.NuGet.Inspection;

namespace Offramp.NuGet.Tests;

/// <summary>The target of <c>newtonsoft scan</c> and <c>upgrade</c>: the newest stable release in a range that supports every framework.</summary>
public sealed class ReleaseChooserTests
{
    private static readonly NuGetFramework[] Frameworks = [NuGetFramework.Parse("net48"), NuGetFramework.Parse("net10.0")];

    [Fact]
    public async Task The_newest_stable_release_in_the_range_is_chosen()
    {
        // The recording has 13.0.1, 13.0.3, and 13.0.4-beta1.
        var chosen = await ReleaseChooser.NewestSupportingAsync(new RecordedPackageFeeds(VersionsFeed.Load()), "Newtonsoft.Json", VersionRange.Parse("[13.0,14.0)"), Frameworks, CancellationToken.None);

        Assert.Equal(NuGetVersion.Parse("13.0.3"), chosen.Version);
        Assert.NotNull(chosen.Package);
        Assert.Null(chosen.Problem);
    }

    [Fact]
    public async Task A_release_that_does_not_support_a_framework_is_passed_over()
    {
        // 3.5.8 ships net20 and net35 assemblies only: nothing a net10.0 project can use.
        var chosen = await ReleaseChooser.NewestSupportingAsync(new RecordedPackageFeeds(VersionsFeed.Load()), "Newtonsoft.Json", VersionRange.Parse("[3.5,3.6)"), Frameworks, CancellationToken.None);

        Assert.Null(chosen.Version);
        Assert.Contains("supports net10.0, net48", chosen.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_range_says_so()
    {
        var chosen = await ReleaseChooser.NewestSupportingAsync(new RecordedPackageFeeds(VersionsFeed.Load()), "Newtonsoft.Json", VersionRange.Parse("[14.0,)"), Frameworks, CancellationToken.None);

        Assert.Null(chosen.Version);
        Assert.StartsWith("no stable release of Newtonsoft.Json", chosen.Problem, StringComparison.Ordinal);
    }
}
