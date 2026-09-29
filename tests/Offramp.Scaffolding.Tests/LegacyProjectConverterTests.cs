using System.Text;
using System.Xml.Linq;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Scaffolding.Csproj;

namespace Offramp.Scaffolding.Tests;

/// <summary>
/// The conversion itself, without a build (<c>csproj modernize</c>'s command tests build the
/// <c>legacy-csproj</c> fixture). Cases from DotNetNuke 9.13.
/// </summary>
public sealed class LegacyProjectConverterTests : IDisposable
{
    private const string Project = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <AssemblyName>Library</AssemblyName>
            <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
          </PropertyGroup>
          <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
            <OutputPath>bin\</OutputPath>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Class1.cs" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\Generators\Generators.csproj" ReferenceOutputAssembly="false" OutputItemType="Analyzer">
              <Project>{11111111-2222-3333-4444-555555555555}</Project>
              <Name>Generators</Name>
            </ProjectReference>
            <ProjectReference Include="..\Windows\Windows.csproj" Condition="'$(OS)' == 'Windows_NT'" />
          </ItemGroup>
          <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
        </Project>
        """;

    private readonly ScratchDirectory _repo = new("convert");

    public LegacyProjectConverterTests() => _repo.Write("src/Library/Class1.cs", "class C {}");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Project_reference_metadata_written_as_attributes_and_conditions_are_kept()
    {
        var project = Convert([]);

        var generators = project.Descendants("ProjectReference").Single(r => (string?)r.Attribute("Include") == @"..\Generators\Generators.csproj");
        Assert.Equal("false", (string?)generators.Attribute("ReferenceOutputAssembly"));
        Assert.Equal("Analyzer", (string?)generators.Attribute("OutputItemType"));
        Assert.Empty(generators.Elements());
        var windows = project.Descendants("ProjectReference").Single(r => (string?)r.Attribute("Include") == @"..\Windows\Windows.csproj");
        Assert.Equal("'$(OS)' == 'Windows_NT'", (string?)windows.Attribute("Condition"));
    }

    [Fact]
    public void Output_stays_in_the_legacy_folder_for_a_single_target_framework()
    {
        var single = Convert([]);
        var dual = Convert(["net472", "net8.0"]);

        Assert.Equal("false", single.Descendants("AppendTargetFrameworkToOutputPath").Single().Value);
        Assert.Equal(@"bin\", single.Descendants("OutputPath").Single().Value);
        Assert.Empty(dual.Descendants("AppendTargetFrameworkToOutputPath"));
    }

    /// <summary>
    /// SmartStoreNET 4.2.0 (P1 #6): the NuGet 2 restore import stayed while SolutionDir went, so a
    /// project built on its own failed with MSB4019. SolutionDir stays while a build event uses it.
    /// </summary>
    [Fact]
    public void The_nuget_2_restore_import_goes_and_solution_dir_stays_while_something_uses_it()
    {
        const string Legacy = """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <AssemblyName>Library</AssemblyName>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <SolutionDir Condition="$(SolutionDir) == '' Or $(SolutionDir) == '*Undefined*'">..\..\</SolutionDir>
                <RestorePackages>true</RestorePackages>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Class1.cs" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <Import Project="$(SolutionDir)\.nuget\nuget.targets" />
              POSTBUILD
            </Project>
            """;

        var (plain, output) = Convert(Legacy.Replace("POSTBUILD", "", StringComparison.Ordinal), []);
        var (used, _) = Convert(Legacy.Replace("POSTBUILD", "<PropertyGroup><PostBuildEvent>copy $(TargetPath) $(SolutionDir)packages\\out</PostBuildEvent></PropertyGroup>", StringComparison.Ordinal), []);

        Assert.DoesNotContain(plain.Descendants("Import"), i => ((string?)i.Attribute("Project"))!.Contains("nuget.targets", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Import NuGet.targets", output.Dropped);
        Assert.Empty(plain.Descendants("SolutionDir"));
        Assert.Empty(plain.Descendants("RestorePackages"));
        var solutionDir = Assert.Single(used.Descendants("SolutionDir"));
        Assert.Equal(@"..\..\", solutionDir.Value);
        Assert.Equal("$(SolutionDir) == '' Or $(SolutionDir) == '*Undefined*'", (string?)solutionDir.Attribute("Condition"));
        Assert.DoesNotContain(used.Descendants("Import"), i => ((string?)i.Attribute("Project"))!.Contains("nuget.targets", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>NHibernate 4.1.2 (P1 #11): an SDK-style project also compiles against its references' references.</summary>
    [Fact]
    public void Transitive_project_references_are_turned_off_when_asked()
    {
        var (flagged, _) = Convert(Project, [], disableTransitiveProjectReferences: true);
        var (plain, _) = Convert(Project, []);

        Assert.Equal("true", flagged.Descendants("DisableTransitiveProjectReferences").Single().Value);
        Assert.Empty(plain.Descendants("DisableTransitiveProjectReferences"));
    }

    /// <summary>
    /// Open Live Writer 0.6.3 (P1 #9): a PostBuildEvent in a conditioned group lost its condition; a
    /// -windows target did not get Windows Forms, and kept the .NET Framework references for every
    /// target; a target that replaced a common target, and an import that sets the output path,
    /// stopped working without a word.
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR4308")]
    public void Build_steps_keep_their_condition_windows_targets_get_the_desktop_framework_and_overrides_are_named()
    {
        _repo.Write("src/Library/build.settings", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <OutputPath>..\..\out\</OutputPath>
                <IntermediateOutputPath Condition="'$(OS)' == 'Windows_NT'">..\..\obj\</IntermediateOutputPath>
              </PropertyGroup>
            </Project>
            """);
        const string Legacy = """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="build.settings" />
              <PropertyGroup>
                <AssemblyName>Library</AssemblyName>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System.Windows.Forms" />
                <Reference Include="System.Deployment" />
                <Reference Include="Vendor.Controls">
                  <HintPath>..\..\lib\Vendor.Controls.dll</HintPath>
                </Reference>
                <Compile Include="Class1.cs" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <PropertyGroup Condition="'$(OfframpCompileOnly)' != 'true'">
                <PostBuildEvent>"$(ProjectDir)createinstaller.cmd"</PostBuildEvent>
              </PropertyGroup>
              <Target Name="_CopyFilesMarkedCopyLocal" />
            </Project>
            """;

        var (windows, output) = Convert(Legacy, ["net472", "net10.0-windows"]);
        var (framework, _) = Convert(Legacy, []);

        var postBuild = windows.Descendants("Target").Single(t => (string?)t.Attribute("Name") == "PostBuild");
        Assert.Equal("'$(OfframpCompileOnly)' != 'true'", (string?)postBuild.Attribute("Condition"));
        Assert.Equal("true", windows.Descendants("UseWindowsForms").Single().Value);
        Assert.Empty(windows.Descendants("UseWPF"));
        var conditioned = windows.Elements("ItemGroup").Single(g => (string?)g.Attribute("Condition") == "'$(TargetFrameworkIdentifier)' == '.NETFramework'");
        Assert.Equal(["System.Windows.Forms", "System.Deployment"], conditioned.Elements("Reference").Select(r => (string?)r.Attribute("Include")));
        Assert.Contains(windows.Elements("ItemGroup").Where(g => g.Attribute("Condition") is null).SelectMany(g => g.Elements("Reference")), r => (string?)r.Attribute("Include") == "Vendor.Controls");
        Assert.Empty(framework.Descendants("UseWindowsForms"));
        Assert.All(framework.Elements("ItemGroup"), g => Assert.Null(g.Attribute("Condition")));
        var notes = output.Notes.Where(n => n.Code == "OFR4308").Select(n => n.Message).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Contains("build.settings sets TargetFrameworkVersion, OutputPath unconditionally", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("The _CopyFilesMarkedCopyLocal target", StringComparison.Ordinal));
    }

    private (XElement Project, ConversionOutput Output) Convert(string project, string[] frameworks, bool disableTransitiveProjectReferences = false)
    {
        var output = LegacyProjectConverter.Convert(new ConversionInput
        {
            RepositoryRoot = _repo.Path,
            Project = new ProjectInfo
            {
                Id = "src/Library/Library.csproj",
                Name = "Library",
                TargetFrameworks = ["net472"],
                Compile = ["src/Library/Class1.cs"],
            },
            Bytes = Encoding.UTF8.GetBytes(project),
            TargetFrameworks = frameworks,
            DisableTransitiveProjectReferences = disableTransitiveProjectReferences,
        });
        return (XDocument.Parse(Encoding.UTF8.GetString(output.Bytes!)).Root!, output);
    }

    private XElement Convert(string[] frameworks)
    {
        var output = LegacyProjectConverter.Convert(new ConversionInput
        {
            RepositoryRoot = _repo.Path,
            Project = new ProjectInfo
            {
                Id = "src/Library/Library.csproj",
                Name = "Library",
                TargetFrameworks = ["net472"],
                Compile = ["src/Library/Class1.cs"],
            },
            Bytes = Encoding.UTF8.GetBytes(Project),
            TargetFrameworks = frameworks,
        });
        return XDocument.Parse(Encoding.UTF8.GetString(output.Bytes!)).Root!;
    }
}
