using System.Text;
using Offramp.Refactoring.Dependencies.PackagesConfig;
using Offramp.Refactoring.ProjectFiles;

namespace Offramp.Refactoring.Tests;

/// <summary>The edits <c>newtonsoft upgrade</c> makes to legacy projects keep every other byte.</summary>
public sealed class PackagesConfigEditorTests
{
    [Fact]
    public void Only_the_version_changes_with_the_byte_order_mark_quotes_and_line_endings_kept()
    {
        var before = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<packages>\r\n  <package id='Newtonsoft.Json'   version='7.0.1' targetFramework=\"net48\" />\r\n  <package id=\"Other\" version=\"7.0.1\" />\r\n</packages>")).ToArray();

        var after = PackagesConfigEditor.SetVersion(before, "newtonsoft.json", "13.0.4")!;

        var expected = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<packages>\r\n  <package id='Newtonsoft.Json'   version='13.0.4' targetFramework=\"net48\" />\r\n  <package id=\"Other\" version=\"7.0.1\" />\r\n</packages>")).ToArray();
        Assert.Equal(expected, after);
    }

    [Fact]
    public void A_package_that_is_not_listed_is_not_an_edit_and_allowed_versions_are_read()
    {
        var content = Encoding.UTF8.GetBytes("<packages>\n  <package id=\"Newtonsoft.Json\" version=\"7.0.1\" allowedVersions=\"[7,8)\" />\n</packages>\n");

        Assert.Null(PackagesConfigEditor.SetVersion(content, "Missing", "1.0.0"));
        Assert.Equal(new PackagesConfigEntry("7.0.1", "[7,8)"), PackagesConfigEditor.Find(content, "Newtonsoft.Json"));
    }

    [Fact]
    public void A_reference_moves_its_identity_and_hint_path_and_nothing_else()
    {
        var before = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Reference Include="Newtonsoft.Json, Version=7.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed, processorArchitecture=MSIL">
                  <HintPath>..\..\packages\Newtonsoft.Json.7.0.1\lib\net45\Newtonsoft.Json.dll</HintPath>
                  <Private>True</Private>
                </Reference>
                <Reference Include="System" />
              </ItemGroup>
            </Project>
            """);
        var editor = ProjectFileEditor.Load(before);

        var changed = editor.RewriteReference("Newtonsoft.Json", "13.0.0.0", "Newtonsoft.Json.7.0.1", "Newtonsoft.Json.13.0.4", "net45");

        Assert.Equal(1, changed);
        Assert.Equal(Encoding.UTF8.GetString(before)
            .Replace("Version=7.0.0.0", "Version=13.0.0.0", StringComparison.Ordinal)
            .Replace("Newtonsoft.Json.7.0.1", "Newtonsoft.Json.13.0.4", StringComparison.Ordinal), Encoding.UTF8.GetString(editor.Save()));
    }
}
