using Offramp.Workspace.Environment;

namespace Offramp.Workspace.Tests;

public sealed class DotnetProbeTests
{
    [Fact]
    public void List_sdks_output_is_parsed_and_sorted_numerically()
    {
        const string output = "10.0.100 [/usr/share/dotnet/sdk]\n8.0.404 [/usr/share/dotnet/sdk]\n9.0.100-rc.2.24474.11 [/x]\n9.0.100 [/x]\n";

        Assert.Equal(["8.0.404", "9.0.100-rc.2.24474.11", "9.0.100", "10.0.100"], DotnetProbe.ParseListSdks(output));
    }

    [Theory]
    [InlineData("10.0.401", 10)]
    [InlineData("8.0.100", 8)]
    [InlineData("11.0.100-preview.1", 11)]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Major_version(string? version, int? major) => Assert.Equal(major, DotnetProbe.Major(version));
}

public sealed class ReferenceAssembliesProbeTests
{
    /// <summary>The real probe against the machine's NuGet configuration.</summary>
    [Fact]
    [Trait("Category", "Network")]
    public async Task The_real_probe_finds_the_package_in_the_cache_or_on_a_feed()
    {
        var result = await new ReferenceAssembliesProbe().ProbeAsync(Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Contains(result.State, new[]
        {
            ReferenceAssembliesState.Cached, ReferenceAssembliesState.TargetingPack, ReferenceAssembliesState.AvailableFromFeed,
        });
    }
}
