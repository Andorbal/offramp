using Offramp.Core.Model;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Tests;

public sealed class TfmTests
{
    [Theory]
    [InlineData("net48", "net48")]
    [InlineData(".NETFramework,Version=v4.8", "net48")]
    [InlineData("NET10.0", "net10.0")]
    [InlineData("net10.0-windows", "net10.0-windows")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData("not a framework", null)]
    [InlineData("", null)]
    public void Normalize_returns_the_short_folder_name(string value, string? expected) =>
        Assert.Equal(expected, Tfm.Normalize(value));

    [Fact]
    public void Legacy_identifier_and_version_combine()
    {
        Assert.Equal("net472", Tfm.FromIdentifier(".NETFramework", "v4.7.2"));
        Assert.Null(Tfm.FromIdentifier(null, "v4.7.2"));
    }

    [Fact]
    public void Sort_puts_framework_then_standard_then_modern_each_by_version()
    {
        Assert.Equal(
            ["net462", "net48", "netstandard2.0", "netstandard2.1", "net8.0", "net10.0"],
            Tfm.Sort(["net10.0", "netstandard2.1", "net48", "net8.0", "netstandard2.0", "net462", "net48"]));
    }

    [Theory]
    [InlineData(FrameworkClass.Framework, "net48")]
    [InlineData(FrameworkClass.Framework, "net472", "net48")]
    [InlineData(FrameworkClass.Standard, "netstandard2.0")]
    [InlineData(FrameworkClass.Modern, "net8.0", "net10.0")]
    [InlineData(FrameworkClass.Dual, "net48", "net10.0")]
    [InlineData(FrameworkClass.Dual, "net48", "netstandard2.0")]
    [InlineData(FrameworkClass.Standard, "netstandard2.0", "net10.0")]
    [InlineData(FrameworkClass.Framework, "uap10.0")]
    [InlineData(FrameworkClass.Framework)]
    public void Classify_follows_the_architecture_rules(FrameworkClass expected, params string[] tfms) =>
        Assert.Equal(expected, Tfm.Classify(tfms));
}
