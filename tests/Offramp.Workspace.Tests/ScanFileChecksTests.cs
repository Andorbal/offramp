using Offramp.Fixtures;

namespace Offramp.Workspace.Tests;

/// <summary>
/// <c>scan</c> reports what the projects' files show, whatever the build got to: the legacy-csproj fixture with a
/// generated, git-ignored source file missing (NHibernate), images in a <c>.resx</c> file, and on Linux a source
/// file in another letter case (docs/decisions/0047-static-checks-of-project-files.md).
/// </summary>
public sealed class ScanFileChecksTests
{
    [Fact]
    [ProducesDiagnostic("OFR0123")]
    [ProducesDiagnostic("OFR0119")]
    public async Task A_scan_names_missing_sources_and_resources_from_the_files_in_one_pass()
    {
        var caseSensitive = false;
        var scanned = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            File.AppendAllText(Path.Combine(root, ".gitignore"), "src/SharedAssemblyInfo.cs\n");
            Directory.CreateDirectory(Path.Combine(root, "src", "Billing", "Helpers"));
            File.WriteAllText(Path.Combine(root, "src", "Billing", "Helpers", "Helper.cs"), "namespace Contoso.Billing { internal static class Helper { } }\n");
            caseSensitive = !File.Exists(Path.Combine(root, "src", "Billing", "helpers", "Helper.cs"));
            File.WriteAllText(Path.Combine(root, "src", "Billing", "Images.resx"), """
                <?xml version="1.0" encoding="utf-8"?>
                <root>
                  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
                  <data name="Logo" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>AQID</value></data>
                </root>
                """);
            var project = Path.Combine(root, "src", "Billing", "Billing.csproj");
            var original = File.ReadAllText(project);
            File.WriteAllText(project, original.Replace(
                "    <Compile Include=\"Invoice.cs\" />\n",
                "    <Compile Include=\"Invoice.cs\" />\n    <Compile Include=\"..\\SharedAssemblyInfo.cs\"><Link>Properties\\SharedAssemblyInfo.cs</Link></Compile>\n"
                + "    <Compile Include=\"helpers\\Helper.cs\" />\n    <EmbeddedResource Include=\"Images.resx\" />\n",
                StringComparison.Ordinal));
            Assert.NotEqual(original, File.ReadAllText(project));
            return request;
        });
        using var _ = scanned.Repository;

        var model = scanned.Outcome.Model!;
        var missing = Assert.Single(model.Diagnostics, d => d.Code == "OFR0123");
        Assert.Equal("src/Billing/Billing.csproj", missing.Project);
        Assert.Equal("src/SharedAssemblyInfo.cs", missing.File);
        Assert.Equal("[\"src/SharedAssemblyInfo.cs\"]", missing.Data["gitIgnored"]!.ToJsonString());
        Assert.Contains("git-ignored, so the repository's own build", missing.Message, StringComparison.Ordinal);

        // Named from the file, whether or not the build reached the resource step.
        var resources = Assert.Single(model.Diagnostics, d => d.Code == "OFR0119");
        Assert.Equal("Needs Windows to build: 1 non-string resource(s) in src/Billing/Images.resx.", resources.Message);
        Assert.Equal("[\"src/Billing/Images.resx\"]", resources.Data["paths"]!.ToJsonString());

        var billing = model.Projects.Single(p => p.Id == "src/Billing/Billing.csproj");
        Assert.Contains("resources", billing.WindowsOnlyBuildSteps);
        if (caseSensitive)
        {
            var pathCase = Assert.Single(model.Diagnostics, d => d.Code == "OFR0117");
            Assert.Equal("Does not build on a case-sensitive file system: src/Billing/helpers/Helper.cs: 'helpers' is 'Helpers' on disk.", pathCase.Message);
            Assert.Contains("path-case", billing.WindowsOnlyBuildSteps);
        }
    }
}
