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
