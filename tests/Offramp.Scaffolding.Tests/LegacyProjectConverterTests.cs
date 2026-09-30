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

        var postBuild = windows.Descendants("Target").Single(t => (string?)t.Attribute("Name") == "SetBuildEvents").Descendants("PostBuildEvent").Single();
        Assert.Equal("'$(PostBuildEvent)' != '' and ('$(OfframpCompileOnly)' != 'true')", (string?)postBuild.Attribute("Condition"));
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

    /// <summary>
    /// SmartStoreNET 4.2.0 (corpus): a PostBuildEvent became a target with the command inline, so
    /// <c>-p:PostBuildEvent=</c> (OFR0115's remedy for compile-only builds) no longer turned it off. The
    /// events stay, and a target that runs before the build sets each one that is set again, when its
    /// macros have values, for the SDK's own targets to run.
    /// </summary>
    [Fact]
    public void Build_events_stay_and_a_target_sets_them_again_for_the_sdk_targets_to_run()
    {
        const string Legacy = """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <AssemblyName>Library</AssemblyName>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <RunPostBuildEvent>OnOutputUpdated</RunPostBuildEvent>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Class1.cs" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <PropertyGroup>
                <PreBuildEvent>echo before $(ProjectName)</PreBuildEvent>
                <PostBuildEvent>if not exist "$(TargetDir)x86" md "$(TargetDir)x86"</PostBuildEvent>
              </PropertyGroup>
            </Project>
            """;

        var (project, _) = Convert(Legacy, []);

        var target = project.Elements("Target").Single();
        Assert.Equal(("SetBuildEvents", "BeforeBuild"), ((string?)target.Attribute("Name"), (string?)target.Attribute("BeforeTargets")));
        Assert.Equal(["PreBuildEvent", "PostBuildEvent"], target.Element("PropertyGroup")!.Elements().Select(e => e.Name.LocalName));
        var postBuild = target.Descendants("PostBuildEvent").Single();
        Assert.Equal("if not exist \"$(TargetDir)x86\" md \"$(TargetDir)x86\"", postBuild.Value);
        Assert.Equal("'$(PostBuildEvent)' != ''", (string?)postBuild.Attribute("Condition"));
        // The body keeps them: -p:PostBuildEvent= empties that definition, and the target then sets nothing.
        Assert.Equal(postBuild.Value, project.Elements("PropertyGroup").Elements("PostBuildEvent").Single().Value);
        Assert.Single(project.Elements("PropertyGroup").Elements("PreBuildEvent"));
        Assert.Empty(project.Descendants("Exec"));
        Assert.Equal("OnOutputUpdated", project.Elements("PropertyGroup").Descendants("RunPostBuildEvent").Single().Value);
    }

    /// <summary>
    /// Open Live Writer 0.6.3 (corpus): 9 projects have .resx files on disk that they do not embed (69 in
    /// OpenLiveWriter.ApplicationFramework). The conversion kept the listed ones, but the SDK's glob embedded the
    /// others too, and verification failed with "resources added".
    /// </summary>
    [Fact]
    public void Resx_files_on_disk_that_the_project_does_not_embed_stay_out()
    {
        _repo.Write("src/Library/Listed.resx", "<root />");
        _repo.Write("src/Library/Forms/Orphan.resx", "<root />");

        var (listed, _) = Convert(Project.Replace("<Compile Include=\"Class1.cs\" />",
            "<Compile Include=\"Class1.cs\" />\n    <EmbeddedResource Include=\"Listed.resx\" Condition=\"'$(Configuration)' != ''\" />", StringComparison.Ordinal), []);
        var (none, _) = Convert(Project, []);

        static List<string> Resources(XElement project) =>
            [.. project.Descendants("EmbeddedResource").Select(e => $"{(string?)e.Attribute("Remove")}|{(string?)e.Attribute("Include")}|{(string?)e.Attribute("Condition")}")];
        Assert.Equal([@"**\*.resx||", "|Listed.resx|'$(Configuration)' != ''"], Resources(listed));
        Assert.Equal([@"**\*.resx||"], Resources(none));
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
