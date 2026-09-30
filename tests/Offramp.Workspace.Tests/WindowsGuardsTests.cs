using System.Text;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;

namespace Offramp.Workspace.Tests;

/// <summary>
/// <see cref="WindowsGuards"/>: the conditions <c>doctor --fix</c> adds to the Windows-only settings a project file
/// sets itself, as text insertions that keep every other byte
/// (docs/decisions/0063-condition-windows-only-settings-in-project-files.md).
/// </summary>
public sealed class WindowsGuardsTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("guards");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Each_windows_only_setting_is_conditioned_where_the_project_sets_it()
    {
        const string project = """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <GenerateSerializationAssemblies>On</GenerateSerializationAssemblies>
                <MvcBuildViews>true</MvcBuildViews>
                <RestorePackages>true</RestorePackages>
                <MSBuildExtensionsPath>$(MSBuildThisFileDirectory)tools\msbuild</MSBuildExtensionsPath>
              </PropertyGroup>
              <Target Name="AfterBuild">
                <Exec Command="xcopy /y &quot;$(TargetPath)&quot; ..\drop\" />
                <Exec Command="dotnet tool run gen" />
              </Target>
              <PropertyGroup>
                <PostBuildEvent>echo built</PostBuildEvent>
              </PropertyGroup>
            </Project>
            """;

        var updated = WindowsGuards.Apply(project);

        Assert.Equal("""
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <GenerateSerializationAssemblies Condition="'$(MSBuildRuntimeType)' != 'Core'">On</GenerateSerializationAssemblies>
                <MvcBuildViews Condition="'$(MSBuildRuntimeType)' != 'Core'">true</MvcBuildViews>
                <RestorePackages Condition="'$(OS)' == 'Windows_NT'">true</RestorePackages>
                <MSBuildExtensionsPath Condition="'$(OS)' == 'Windows_NT'">$(MSBuildThisFileDirectory)tools\msbuild</MSBuildExtensionsPath>
              </PropertyGroup>
              <Target Name="AfterBuild">
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="xcopy /y &quot;$(TargetPath)&quot; ..\drop\" />
                <Exec Command="dotnet tool run gen" />
              </Target>
              <PropertyGroup>
                <PostBuildEvent Condition="'$(OS)' == 'Windows_NT'">echo built</PostBuildEvent>
              </PropertyGroup>
            </Project>
            """, updated);
        Assert.Equal(
            [(3, "GenerateSerializationAssemblies", "sgen"), (4, "MvcBuildViews", "aspnet-compiler"), (5, "RestorePackages", "nuget-restore"),
             (6, "MSBuildExtensionsPath", "msbuild-extensions-path"), (9, "Exec in target AfterBuild", "build-event"), (13, "PostBuildEvent", "build-event")],
            WindowsGuards.Find(project).Needed.Select(g => (g.Line, g.Setting, g.Step)));
    }

    [Fact]
    public void Settings_that_ask_for_nothing_windows_only_are_left_alone()
    {
        const string project = """
            <Project>
              <PropertyGroup>
                <GenerateSerializationAssemblies>Off</GenerateSerializationAssemblies>
                <MvcBuildViews>false</MvcBuildViews>
                <RestorePackages>false</RestorePackages>
                <PostBuildEvent></PostBuildEvent>
              </PropertyGroup>
              <Target Name="Batch">
                <Exec Command="echo %(Compile.Identity)" />
              </Target>
            </Project>
            """;

        Assert.Equal(project, WindowsGuards.Apply(project));
        Assert.Empty(WindowsGuards.Find(project).Needed);
        Assert.Empty(WindowsGuards.Find(project).Guarded);
    }

    [Fact]
    public void A_condition_the_setting_has_is_kept_inside_the_guard()
    {
        const string project = """
            <Project>
              <PropertyGroup>
                <GenerateSerializationAssemblies Condition=" '$(Configuration)' == 'Release' ">On</GenerateSerializationAssemblies>
                <PostBuildEvent Condition='$(Deploy) == true'>copy a b</PostBuildEvent>
                <PreBuildEvent Condition="">del obj\*.tmp</PreBuildEvent>
              </PropertyGroup>
            </Project>
            """;

        Assert.Equal("""
            <Project>
              <PropertyGroup>
                <GenerateSerializationAssemblies Condition="'$(MSBuildRuntimeType)' != 'Core' And ('$(Configuration)' == 'Release')">On</GenerateSerializationAssemblies>
                <PostBuildEvent Condition='&apos;$(OS)&apos; == &apos;Windows_NT&apos; And ($(Deploy) == true)'>copy a b</PostBuildEvent>
                <PreBuildEvent Condition="'$(OS)' == 'Windows_NT'">del obj\*.tmp</PreBuildEvent>
              </PropertyGroup>
            </Project>
            """, WindowsGuards.Apply(project));
    }

    [Fact]
    public void Settings_already_chosen_by_platform_are_reported_as_guarded_and_not_edited()
    {
        const string project = """
            <Project>
              <PropertyGroup Condition="'$(OS)' == 'Windows_NT'">
                <PostBuildEvent>xcopy a b</PostBuildEvent>
              </PropertyGroup>
              <PropertyGroup>
                <GenerateSerializationAssemblies Condition="'$(OfframpCompileOnly)' != 'true'">On</GenerateSerializationAssemblies>
              </PropertyGroup>
              <Target Name="Sign" Condition="$([MSBuild]::IsOSPlatform('Windows'))">
                <Exec Command="signtool.exe sign $(TargetPath)" />
              </Target>
              <Choose>
                <When Condition="'$(MSBuildRuntimeType)' == 'Full'">
                  <PropertyGroup>
                    <MvcBuildViews>true</MvcBuildViews>
                  </PropertyGroup>
                </When>
              </Choose>
            </Project>
            """;

        var found = WindowsGuards.Find(project);

        Assert.Equal(project, WindowsGuards.Apply(project));
        Assert.Empty(found.Needed);
        Assert.Equal(["PostBuildEvent", "GenerateSerializationAssemblies", "Exec in target Sign", "MvcBuildViews"], found.Guarded.Select(g => g.Setting));
    }

    [Fact]
    public void A_second_fix_changes_nothing()
    {
        const string project = "<Project><PropertyGroup><GenerateSerializationAssemblies>On</GenerateSerializationAssemblies><PostBuildEvent>xcopy a b</PostBuildEvent></PropertyGroup></Project>";

        var once = WindowsGuards.Apply(project);

        Assert.NotEqual(project, once);
        Assert.Equal(once, WindowsGuards.Apply(once));
        Assert.Empty(WindowsGuards.Find(once).Needed);
        Assert.Equal(2, WindowsGuards.Find(once).Guarded.Count);
    }

    [Fact]
    public void Writing_keeps_the_byte_order_mark_the_line_endings_and_everything_else()
    {
        var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project>\r\n  <!-- sgen -->\r\n  <PropertyGroup>\r\n\t<GenerateSerializationAssemblies>On</GenerateSerializationAssemblies>\r\n  </PropertyGroup>\r\n  <PropertyGroup>\r\n    <PostBuildEvent>copy \"$(TargetPath)\" ..\\drop\r\ndel *.tmp</PostBuildEvent>\r\n  </PropertyGroup>\r\n</Project>\r\n";
        _repo.WriteBytes("src/A/A.csproj", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]);

        var plan = WindowsGuards.Plan(_repo.Path, "src/A/A.csproj")!;
        var applied = WindowsGuards.Apply(_repo.Path, "src/A/A.csproj")!;

        var expected = text
            .Replace("<GenerateSerializationAssemblies>", "<GenerateSerializationAssemblies Condition=\"'$(MSBuildRuntimeType)' != 'Core'\">", StringComparison.Ordinal)
            .Replace("<PostBuildEvent>", "<PostBuildEvent Condition=\"'$(OS)' == 'Windows_NT'\">", StringComparison.Ordinal);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(Path.Combine(_repo.Path, "src/A/A.csproj")));
        Assert.False(plan.Applied);
        Assert.True(applied.Applied);
        Assert.Equal([5, 8], applied.Guards.Select(g => g.Line));
        Assert.Contains("+\t<GenerateSerializationAssemblies Condition=", plan.Diff, StringComparison.Ordinal);
        Assert.Null(WindowsGuards.Plan(_repo.Path, "src/A/A.csproj"));
    }

    [Fact]
    public void A_file_in_a_legacy_code_page_is_never_rewritten()
    {
        // "Café" in Windows-1252: not valid UTF-8, and decoding it as UTF-8 would corrupt it on the way back.
        byte[] bytes = [.. Encoding.ASCII.GetBytes("<Project><PropertyGroup><Company>Caf"), 0xE9, .. Encoding.ASCII.GetBytes("</Company><PostBuildEvent>xcopy a b</PostBuildEvent></PropertyGroup></Project>")];
        _repo.WriteBytes("src/A/A.csproj", bytes);

        Assert.Null(WindowsGuards.Apply(_repo.Path, "src/A/A.csproj"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_repo.Path, "src/A/A.csproj")));
    }

    [Fact]
    public void The_files_are_the_projects_the_shared_files_above_them_and_the_repository_files_they_import()
    {
        _repo.Write("Directory.Build.props", "<Project />\n");
        _repo.Write("src/Directory.Build.targets", "<Project />\n");
        _repo.Write("build/writer.build.settings", "<Project><Import Project=\"common.targets\" /></Project>\n");
        _repo.Write("build/common.targets", "<Project />\n");
        _repo.Write("src/A/A.csproj", """
            <Project>
              <Import Project="..\..\build\writer.build.settings" />
              <Import Project="$(MSBuildThisFileDirectory)..\..\packages\Foo.1.0\build\Foo.targets" />
              <Import Project="$(SolutionDir)\.nuget\NuGet.targets" />
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);
        _repo.Write("packages/Foo.1.0/build/Foo.targets", "<Project />\n");
        _repo.Write("src/B/B.vcxproj", "<Project />\n");

        var files = WindowsGuards.Files(_repo.Path, ["src/A/A.csproj", "src/B/B.vcxproj", "src/Missing/Missing.csproj"], ["tools/extra.props"]);

        Assert.Equal(["Directory.Build.props", "build/common.targets", "build/writer.build.settings", "src/A/A.csproj", "src/Directory.Build.targets"], files);
    }
}
