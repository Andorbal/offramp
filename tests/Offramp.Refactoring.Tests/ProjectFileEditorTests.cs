using System.Text;
using Offramp.Refactoring.ProjectFiles;

namespace Offramp.Refactoring.Tests;

public sealed class ProjectFileEditorTests
{
    private const string Sdk = """
        <Project Sdk="Microsoft.NET.Sdk">
          <!-- keep me -->
          <PropertyGroup>
            <TargetFramework>net48</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="NUnit" Version="3.14.0" />
            <PackageReference Include="Serilog" Version="2.0.0" />
          </ItemGroup>
          <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
            <Reference Include="System.Web" />
          </ItemGroup>
        </Project>

        """;

    [Fact]
    public void Adds_items_in_order_next_to_their_kind_and_keeps_everything_else()
    {
        var editor = ProjectFileEditor.Load(Bytes(Sdk));
        editor.AddPackageReference("Moq", "4.20.72");
        editor.AddProjectReference("../Bar/Bar.csproj");
        editor.AddInternalsVisibleTo("Foo.Tests");

        var text = Encoding.UTF8.GetString(editor.Save());

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- keep me -->
              <PropertyGroup>
                <TargetFramework>net48</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Moq" Version="4.20.72" />
                <PackageReference Include="NUnit" Version="3.14.0" />
                <PackageReference Include="Serilog" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
                <Reference Include="System.Web" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="..\Bar\Bar.csproj" />
              </ItemGroup>
              <ItemGroup>
                <InternalsVisibleTo Include="Foo.Tests" />
              </ItemGroup>
            </Project>

            """, text);
    }

    [Fact]
    public void Removing_the_last_item_removes_its_group()
    {
        var editor = ProjectFileEditor.Load(Bytes(Sdk));

        Assert.Equal(1, editor.RemoveItems("PackageReference", "nunit"));
        Assert.Equal(1, editor.RemoveItems("PackageReference", "Serilog"));
        Assert.Equal(0, editor.RemoveItems("Reference", "System.Web"));

        var text = Encoding.UTF8.GetString(editor.Save());
        Assert.DoesNotContain("PackageReference", text, StringComparison.Ordinal);
        Assert.Contains("<Reference Include=\"System.Web\" />", text, StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(text, "<ItemGroup"));
    }

    [Fact]
    public void Byte_order_mark_and_crlf_survive_an_edit()
    {
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Sdk.Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();
        var editor = ProjectFileEditor.Load(original);
        editor.AddPackageReference("Moq", "4.20.72");

        var saved = editor.Save();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, saved[..3]);
        var text = Encoding.UTF8.GetString(saved[3..]);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Moq\" Version=\"4.20.72\" />\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unchanged_project_saves_to_the_same_bytes()
    {
        var bytes = Bytes(Sdk);

        Assert.Equal(bytes, ProjectFileEditor.Load(bytes).Save());
    }

    [Fact]
    public void Existing_items_are_not_duplicated()
    {
        var editor = ProjectFileEditor.Load(Bytes(Sdk));
        editor.AddPackageReference("nunit", "3.14.0");

        Assert.Equal(Sdk, Encoding.UTF8.GetString(editor.Save()));
        Assert.True(editor.IsSdkStyle);
    }

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);
}
