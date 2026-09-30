using System.Text.Json;
using Offramp.Core.Configuration;
using Offramp.Core.Json;
using Offramp.Core.Model;

namespace Offramp.Core.Tests;

/// <summary>
/// ADR 0057: a target is a .NET major version (shorthand for <c>netN.0</c>), a .NET target framework, or .NET
/// Standard (NHibernate P1 #7: a library's natural target, <c>netstandard2.0</c>, was not expressible).
/// </summary>
public sealed class ModernTargetTests
{
    [Theory]
    [InlineData("10", "net10.0", 10, false, true)]
    [InlineData("5", "net5.0", 5, false, true)]
    [InlineData("net8.0", "net8.0", 8, false, false)]
    [InlineData("net10.0-windows", "net10.0-windows", 10, true, false)]
    [InlineData("net100.0", "net100.0", 100, false, false)]
    [InlineData("netstandard2.0", "netstandard2.0", null, false, false)]
    [InlineData("netstandard2.1", "netstandard2.1", null, false, false)]
    public void A_target_is_an_integer_or_a_target_framework(string text, string moniker, int? major, bool windows, bool number)
    {
        Assert.True(ModernTarget.TryParse(text, out var target));
        Assert.Equal((moniker, major, windows, number, text), (target.Moniker, target.Major, target.Windows, target.WrittenAsNumber, target.Written));
        Assert.Equal(major ?? ModernTarget.DefaultMajor, target.RuntimeMajor);
        Assert.Equal(major is null, target.IsStandard);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("010")]
    [InlineData("ten")]
    [InlineData("net48")]
    [InlineData("net4.8")]
    [InlineData("net4.0")]
    [InlineData("net10")]
    [InlineData("net10.1")]
    [InlineData("NET10.0")]
    [InlineData("net10.0-linux")]
    [InlineData("netstandard1.6")]
    [InlineData("netstandard2.0-windows")]
    [InlineData("netcoreapp3.1")]
    public void Anything_else_is_not_a_target(string text)
    {
        Assert.False(ModernTarget.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ModernTarget.Parse(text));
    }

    [Fact]
    public void The_integer_is_shorthand_for_the_framework_and_both_are_written_back_as_given()
    {
        Assert.Equal(ModernTarget.Parse("10"), ModernTarget.Parse("net10.0"));
        Assert.Equal(ModernTarget.Default, ModernTarget.FromMajor(10));
        Assert.NotEqual(ModernTarget.Parse("net10.0"), ModernTarget.Parse("net10.0-windows"));

        var options = OfframpJson.Options;
        Assert.Equal("10", JsonSerializer.Serialize(ModernTarget.Default, options));
        Assert.Equal("\"netstandard2.0\"", JsonSerializer.Serialize(ModernTarget.Parse("netstandard2.0"), options));
        Assert.Equal("net8.0", JsonSerializer.Deserialize<ModernTarget>("8", options)!.Moniker);
        Assert.Equal("netstandard2.0", JsonSerializer.Deserialize<ModernTarget>("\"netstandard2.0\"", options)!.Moniker);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModernTarget>("\"net48\"", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ModernTarget>("4", options));
    }

    [Fact]
    public void Each_project_moves_to_what_the_target_gives_its_kind()
    {
        var library = new ProjectInfo { Id = "src/Core/Core.csproj", Name = "Core", Kind = ProjectKind.Library };
        var test = library with { Kind = ProjectKind.Test };
        var console = library with { Kind = ProjectKind.Console };
        var forms = library with { AssemblyReferences = [new AssemblyReferenceInfo { Name = "System.Windows.Forms", Kind = AssemblyReferenceKind.Framework }] };

        var net = ModernTarget.Parse("net8.0");
        Assert.Equal(["net8.0", "net8.0", "net8.0", "net8.0-windows"], new[] { library, test, console, forms }.Select(net.For));

        var windows = ModernTarget.Parse("net8.0-windows");
        Assert.Equal(["net8.0-windows", "net8.0-windows", "net8.0-windows", "net8.0-windows"], new[] { library, test, console, forms }.Select(windows.For));

        var standard = ModernTarget.Parse("netstandard2.0");
        Assert.Equal(["netstandard2.0", "net10.0", "net10.0", "net10.0-windows"], new[] { library, test, console, forms }.Select(standard.For));
    }
}
