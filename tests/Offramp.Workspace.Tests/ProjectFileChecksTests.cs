using Offramp.Fixtures;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

/// <summary>
/// The static checks of a project's files (docs/decisions/0047-static-checks-of-project-files.md): letter case,
/// non-string resources, and missing sources, found in one pass instead of one build at a time.
/// </summary>
public sealed class ProjectFileChecksTests : IDisposable
{
    private const string Header = """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
          <assembly alias="System.Windows.Forms" name="System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" />
          <!-- The standard header's example: <data name="Bitmap1" mimetype="application/x-microsoft.net.object.binary.base64"> -->
        """;

    private readonly ScratchDirectory _repo = new("file-checks");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Every_misspelled_import_item_and_resx_file_reference_is_found_in_one_pass()
    {
        // SmartStoreNET needed four scans for 14 of these; Open Live Writer one per build step. A Content
        // item that is not copied to the output is read only by publishing, so it does not count, and neither
        // does an import whose Exists condition is false here. SmartStoreNET's GoogleMerchantCenter imports
        // nuget.targets when NuGet.targets exists, which it does.
        _repo.Write("src/.nuget/NuGet.targets", "<Project />");
        _repo.Write("src/build/Common.props", "<Project />");
        _repo.Write("src/A/Collections/MultiMap.cs", "class M {}");
        _repo.Write("src/A/Migrations/Init.Designer.cs", "class I {}");
        _repo.Write("src/A/CommandBitmaps/Effects.png", "png");
        _repo.Write("src/A/Images.resx", Header + """
              <data name="Effects" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>commandbitmaps\effects.png;System.Byte[], mscorlib</value></data>
            </root>
            """);
        _repo.Write("intl/markets/master.xml", "<markets />");
        _repo.Write("src/A/Web.config", "<configuration />");
        _repo.Write("src/other/skipped.targets", "<Project />");
        Assert.SkipWhen(File.Exists(_repo.Combine("src", ".nuget", "nuget.targets")), "This file system ignores letter case (Windows, macOS), so every spelling exists.");
        var project = _repo.Write("src/A/A.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(SolutionDir)\.nuget\nuget.targets" Condition="Exists('$(SolutionDir)\.nuget\NuGet.targets')" />
              <Import Project="..\Other\Skipped.targets" Condition="Exists('..\Other\Skipped.targets')" />
              <Import Project="..\Build\Common.props" />
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <ItemGroup>
                <Compile Include="Collections\Multimap.cs" />
                <Compile Include="Migrations\Init.designer.cs" />
                <Compile Include="Missing\Conditioned.cs" Condition="'$(Configuration)' == 'Special'" />
                <EmbeddedResource Include="Images.resx" />
                <None Include="..\..\intl\markets\Master.xml"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>
                <Content Include="web.CONFIG" />
              </ItemGroup>
            </Project>
            """);

        var findings = Check(project, solutionDirectory: _repo.Combine("src"));

        Assert.Equal(
            [
                "src/.nuget/nuget.targets: 'nuget.targets' is 'NuGet.targets' on disk",
                "src/Build/Common.props: 'Build' is 'build' on disk",
                "src/A/Collections/Multimap.cs: 'Multimap.cs' is 'MultiMap.cs' on disk",
                "src/A/Migrations/Init.designer.cs: 'Init.designer.cs' is 'Init.Designer.cs' on disk",
                "intl/markets/Master.xml: 'Master.xml' is 'master.xml' on disk",
                "src/A/commandbitmaps/effects.png: 'commandbitmaps' is 'CommandBitmaps' on disk (ResXFileRef in src/A/Images.resx)",
            ],
            findings.CaseMismatches.Select(m => Relative(m.Evidence)));
        Assert.Empty(findings.MissingSources);
        Assert.Empty(findings.NonStringResources);
    }

    [Fact]
    public void Items_from_the_evaluation_are_checked_too()
    {
        // A shared .targets file can add items the project file does not list.
        _repo.Write("src/Shared/Version.cs", "class V {}");
        var project = _repo.Write("src/A/A.csproj", "<Project />");
        Assert.SkipWhen(File.Exists(_repo.Combine("src", "shared", "Version.cs")), "This file system ignores letter case.");
        var evaluation = new EvaluatedProject
        {
            ProjectFile = project,
            TargetFramework = "net48",
            Properties = new Dictionary<string, string>(),
            Items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>
            {
                ["Compile"] = [new EvaluatedItem(@"..\shared\Version.cs", new Dictionary<string, string>())],
            },
        };

        var findings = ProjectFileChecks.Check(_repo.Path, project, [evaluation], p => p, null);

        Assert.Equal("src/shared/Version.cs: 'shared' is 'Shared' on disk", Relative(Assert.Single(findings.CaseMismatches).Evidence));
    }

    [Fact]
    public void Non_string_resources_are_counted_as_MSBuilds_reader_counts_them()
    {
        // Checked against the .NET 10 SDK: only the last three need preserialized resources (MSB3822/MSB3823).
        _repo.Write("src/A/Strings.resx", Header + """
              <data name="Plain" xml:space="preserve"><value>x</value></data>
              <data name="Typed" type="System.String"><value>x</value></data>
              <data name="Qualified" type="System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"><value>x</value></data>
              <data name="Bytes" type="System.Byte[], mscorlib"><value>AQID</value></data>
              <data name="Text" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>t.txt;System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;Windows-1252</value></data>
              <data name="Stream" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>t.txt;System.IO.MemoryStream, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></data>
            </root>
            """);
        _repo.Write("src/A/Images.resx", Header + """
              <data name="Logo" type="System.Resources.ResXFileRef, System.Windows.Forms"><value>logo.png;System.Drawing.Bitmap, System.Drawing</value></data>
              <data name="Icon" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>AQID</value></data>
              <data name="Size" type="System.Drawing.Size, System.Drawing"><value>10, 20</value></data>
            </root>
            """);
        _repo.Write("src/A/t.txt", "text");
        _repo.Write("src/A/logo.png", "png");
        var project = _repo.Write("src/A/A.csproj", """
            <Project>
              <ItemGroup>
                <EmbeddedResource Include="Strings.resx" />
                <EmbeddedResource Include="Images.resx" />
              </ItemGroup>
            </Project>
            """);

        var findings = Check(project);

        var file = Assert.Single(findings.NonStringResources);
        Assert.Equal("src/A/Images.resx", Relative(file.Path));
        Assert.Equal(3, file.Count);
        Assert.Empty(findings.CaseMismatches);
    }

    [Fact]
    public void A_compile_item_missing_in_every_letter_case_is_a_missing_source_unless_the_build_writes_it()
    {
        // NHibernate links ..\SharedAssemblyInfo.cs, which its NAnt build generates.
        var project = _repo.Write("src/NHibernate/NHibernate.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Compile Include="..\SharedAssemblyInfo.cs"><Link>SharedAssemblyInfo.cs</Link></Compile>
                <Compile Include="obj\Generated.cs" />
                <Compile Include="$(IntermediateOutputPath)Other.cs" />
                <None Include="missing.txt" />
              </ItemGroup>
            </Project>
            """);

        var findings = Check(project);

        Assert.Equal(["src/SharedAssemblyInfo.cs"], findings.MissingSources.Select(Relative));
        Assert.Empty(findings.CaseMismatches);
    }

    [Fact]
    public void A_source_file_the_build_itself_writes_is_not_a_missing_source()
    {
        // Open Live Writer: writer.build.targets writes GlobalAssemblyVersionInfo.cs before CoreCompile; a build that
        // stopped earlier leaves it missing, and fixing that build is the remedy, not a script outside MSBuild.
        var targets = _repo.Write("writer.build.targets", """
            <Project>
              <PropertyGroup><GlobalAssemblyVersionInfoPath>$(SrcManagedRoot)\GlobalAssemblyVersionInfo.cs</GlobalAssemblyVersionInfoPath></PropertyGroup>
              <Target Name="GenerateVersionFiles" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(GlobalAssemblyVersionInfoPath)" Lines="x" Overwrite="true" />
              </Target>
            </Project>
            """);
        var imported = _repo.Write("src/Gen/Gen.csproj", """
            <Project>
              <ItemGroup><Compile Include="..\GlobalAssemblyVersionInfo.cs" /></ItemGroup>
            </Project>
            """);
        var local = _repo.Write("src/Local/Local.csproj", """
            <Project>
              <ItemGroup><Compile Include="Generated\Version.cs" /></ItemGroup>
              <Target Name="WriteVersion" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="Generated\Version.cs" Lines="x" Overwrite="true" />
              </Target>
            </Project>
            """);
        var evaluation = new EvaluatedProject
        {
            ProjectFile = imported,
            TargetFramework = "net461",
            Properties = new Dictionary<string, string>(),
            Items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>(),
            Imports = [targets],
        };

        Assert.Empty(ProjectFileChecks.Check(_repo.Path, imported, [evaluation], p => p, null).MissingSources);
        Assert.Equal(["src/GlobalAssemblyVersionInfo.cs"], Check(imported).MissingSources.Select(Relative));

        // In Open Live Writer the project that compiles it does not import the targets; the others do.
        var solutionWide = ProjectFileChecks.WrittenByBuild([targets], [imported, local]);
        Assert.Empty(ProjectFileChecks.Check(_repo.Path, imported, [], p => p, null, solutionWide).MissingSources);
        Assert.Empty(Check(local).MissingSources);
    }

    [Fact]
    public void A_project_whose_files_are_all_there_has_no_findings()
    {
        _repo.Write("src/A/Code.cs", "class C {}");
        var project = _repo.Write("src/A/A.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <ItemGroup>
                <Compile Include="Code.cs;@(Other)" />
                <Compile Include="**\*.cs" />
                <Compile Remove="Gone.cs" />
              </ItemGroup>
              <Target Name="Generate">
                <ItemGroup><Compile Include="Generated\Later.cs" /></ItemGroup>
              </Target>
            </Project>
            """);

        Assert.True(Check(project).IsEmpty);
    }

    private ProjectFileFindings Check(string project, string? solutionDirectory = null) =>
        ProjectFileChecks.Check(_repo.Path, project, [], p => p, solutionDirectory);

    private string Relative(string text) => text.Replace(_repo.Path + "/", "", StringComparison.Ordinal);
}
