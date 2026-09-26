using System.Text;
using System.Xml.Linq;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;

namespace Offramp.Workspace.Tests;

public sealed class CompileOnlyConditionalTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("compile-only");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Inserts_the_block_before_the_closing_element_and_keeps_everything_else()
    {
        const string current = "<Project>\n  <!-- keep me -->\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n";

        var updated = CompileOnlyConditional.Apply(current)!;

        Assert.StartsWith("<Project>\n  <!-- keep me -->\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n", updated, StringComparison.Ordinal);
        Assert.EndsWith("    <OfframpCompileOnly>true</OfframpCompileOnly>\n  </PropertyGroup>\n</Project>\n", updated, StringComparison.Ordinal);
        AssertValidProject(updated);
    }

    [Fact]
    public void Keeps_crlf_line_endings()
    {
        const string current = "<Project>\r\n  <PropertyGroup />\r\n</Project>\r\n";

        var updated = CompileOnlyConditional.Apply(current)!;

        Assert.DoesNotContain("\n", updated.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("  <PropertyGroup Condition=\"!$([MSBuild]::IsOSPlatform('Windows'))\">\r\n", updated, StringComparison.Ordinal);
        AssertValidProject(updated);
    }

    [Fact]
    public void Handles_a_closing_element_on_the_same_line()
    {
        var updated = CompileOnlyConditional.Apply("<Project><PropertyGroup /></Project>")!;

        Assert.StartsWith("<Project><PropertyGroup />\n", updated, StringComparison.Ordinal);
        Assert.EndsWith("</Project>", updated, StringComparison.Ordinal);
        AssertValidProject(updated);
    }

    [Fact]
    public void Creates_a_file_when_there_is_none_and_is_idempotent()
    {
        var created = CompileOnlyConditional.Apply(null)!;

        AssertValidProject(created);
        Assert.Null(CompileOnlyConditional.Apply(created));
    }

    [Fact]
    public void A_file_without_a_closing_element_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CompileOnlyConditional.Apply("<Project"));
    }

    [Fact]
    public void Doctor_fix_plans_without_writing_and_applies_preserving_the_bom()
    {
        var path = _repo.Combine("Directory.Build.props");
        File.WriteAllText(path, "<Project>\n</Project>\n", new UTF8Encoding(true));

        var plan = DoctorRunner.PlanFix(_repo.Path);

        Assert.False(plan.Applied);
        Assert.False(plan.AlreadyPresent);
        Assert.Contains("+  <PropertyGroup Condition", plan.Diff, StringComparison.Ordinal);
        Assert.Equal("<Project>\n</Project>\n", File.ReadAllText(path));

        var applied = DoctorRunner.ApplyFix(_repo.Path);

        Assert.True(applied.Applied);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains(CompileOnlyConditional.Marker, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var second = DoctorRunner.PlanFix(_repo.Path);
        Assert.True(second.AlreadyPresent);
        Assert.Null(second.Diff);
    }

    [Fact]
    public async Task MSBuild_sets_the_properties_only_off_windows()
    {
        _repo.Write("Directory.Build.props", "<Project>\n</Project>\n");
        _repo.Write("A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        DoctorRunner.ApplyFix(_repo.Path);

        var result = await Offramp.Core.Processes.ProcessRunner.Instance.RunAsync(
            new Offramp.Core.Processes.ProcessSpec("dotnet", ["msbuild", "A/A.csproj", "-getProperty:OfframpCompileOnly", "-nologo"]) { WorkingDirectory = _repo.Path },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.StandardOutput + result.StandardError);
        Assert.Equal(OperatingSystem.IsWindows() ? "" : "true", result.StandardOutput.Trim());
    }

    private static void AssertValidProject(string content)
    {
        var document = XDocument.Parse(content);
        Assert.Equal("Project", document.Root!.Name.LocalName);
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "OfframpCompileOnly" && e.Value == "true");
    }
}
