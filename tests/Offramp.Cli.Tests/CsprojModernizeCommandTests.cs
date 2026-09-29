using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>csproj modernize</c> on the <c>legacy-csproj</c> fixture: SDK-style projects that compile exactly what the legacy ones did.</summary>
public sealed class CsprojModernizeCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR4301")]
    [ProducesDiagnostic("OFR4302")]
    public async Task Legacy_projects_become_sdk_style_and_compile_the_same_inputs()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-csproj");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        Assert.All(fixture.Outcome.Model!.Projects, p => Assert.NotEmpty(p.CompilerCalls));
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("csproj", "modernize", "--all", "--json");

        Assert.True(dryRun.ExitCode == 0, dryRun.Out);
        SchemaAssert.ValidEnvelope(dryRun.Out, "csproj-modernize");
        var node = JsonNode.Parse(dryRun.Out)!;
        var projects = node["result"]!["projects"]!.AsArray();
        Assert.All(projects, p => Assert.True(p!["verification"]!["passed"]!.GetValue<bool>(), p.ToJsonString()));
        var tool = projects.Single(p => p!["project"]!.GetValue<string>() == "src/Billing.Tool/Billing.Tool.csproj")!;
        Assert.Equal("explicit", tool["compileItems"]!.GetValue<string>());
        Assert.Equal(["Newtonsoft.Json"], tool["verification"]!["targets"]![0]!["transitiveReferencesAdded"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(["OFR4301", "OFR4302"], node["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).Where(c => c.StartsWith("OFR43", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.StartsWith("<?xml", repository.Directory.Read("src/Billing/Billing.csproj").TrimStart('\uFEFF'), StringComparison.Ordinal);
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");

        var applied = await cli.RunAsync("csproj", "modernize", "--all", "--apply", "--json");

        Assert.True(applied.ExitCode == 0, applied.Out);
        var billing = repository.Directory.Read("src/Billing/Billing.csproj");
        Assert.StartsWith("<Project Sdk=\"Microsoft.NET.Sdk\">", billing.TrimStart('\uFEFF'), StringComparison.Ordinal);
        Assert.True(File.ReadAllBytes(repository.Directory.Combine("src", "Billing", "Billing.csproj")).AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]), "The byte order mark is kept.");
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", billing, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>2.3.1.0</FileVersion>", billing, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyTitle(", repository.Directory.Read("src/Billing/Properties/AssemblyInfo.cs"), StringComparison.Ordinal);
        Assert.Contains("InternalsVisibleTo(\"Billing.Tool\")", repository.Directory.Read("src/Billing/Properties/AssemblyInfo.cs"), StringComparison.Ordinal);
        Assert.False(repository.Directory.Exists("src/Billing/packages.config"));
        Assert.Contains("<Compile Include=\"..\\Shared\\Version.cs\">", repository.Directory.Read("src/Billing.Tool/Billing.Tool.csproj"), StringComparison.Ordinal);

        // The converted projects build without the packages folder, and a rescan sees SDK-style projects.
        Directory.Delete(repository.Directory.Combine("packages"), recursive: true);
        var rescan = await cli.RunAsync("scan", "--json");
        Assert.True(rescan.ExitCode == 0, rescan.Out);
        var model = JsonNode.Parse(File.ReadAllText(repository.Directory.Combine(".offramp", "workspace.json")))!;
        Assert.All(model["projects"]!.AsArray(), p => Assert.True(p!["sdkStyle"]!.GetValue<bool>()));
    }

    [Fact]
    [ProducesDiagnostic("OFR4303")]
    public async Task A_conversion_that_compiles_differently_is_not_applied_unless_accepted()
    {
        var fixture = await ScannedFixtures.GetAsync("legacy-csproj");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();
        var before = MoveCommandTests.Tree(fixture.Root);

        // net10.0 has no System.Configuration.ConfigurationSection: the added target cannot build.
        var refused = await cli.RunAsync("csproj", "modernize", "--project", "Billing", "--tfm", "net48;net10.0", "--apply", "--json");

        Assert.Equal(1, refused.ExitCode);
        var node = JsonNode.Parse(refused.Out)!;
        Assert.False(node["result"]!["applied"]!.GetValue<bool>());
        var failure = Assert.Single(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4303");
        Assert.Contains("does not build", failure!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(node["result"]!["projects"]![0]!["verification"]!["buildErrors"]!.AsArray());
        Assert.Equal(before, MoveCommandTests.Tree(fixture.Root));
    }

    [Fact]
    [ProducesDiagnostic("OFR4305")]
    public async Task Known_vulnerabilities_that_warnings_as_errors_stop_are_reported_not_counted_against_the_conversion()
    {
        // Billing treats warnings as errors; Newtonsoft.Json 12.0.1 has a known vulnerability (NU1903),
        // which NuGet audit reports once the project restores the PackageReference way.
        var fixture = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            foreach (var file in new[] { "src/Billing/Billing.csproj", "src/Billing/packages.config" })
            {
                var path = Path.Combine(root, file);
                File.WriteAllText(path, File.ReadAllText(path)
                    .Replace("13.0.3", "12.0.1", StringComparison.Ordinal)
                    .Replace("<LangVersion>7.3</LangVersion>", "<LangVersion>7.3</LangVersion>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>", StringComparison.Ordinal));
            }

            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("csproj", "modernize", "--project", "Billing", "--json");

        Assert.True(run.ExitCode == 0, run.Out);
        var node = JsonNode.Parse(run.Out)!;
        Assert.True(node["result"]!["projects"]![0]!["verification"]!["passed"]!.GetValue<bool>(), run.Out);
        var audit = Assert.Single(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4305");
        Assert.Contains("NU1903", audit!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4303");
    }

    /// <summary>
    /// NHibernate 4.1.2 (P0 #5), SmartStoreNET 4.2.0 (P0 #1), Open Live Writer 0.6.3 (P0 #3, P1 #9): the
    /// assemblyinfo codemod stripped a linked SharedAssemblyInfo.cs that other projects compile, and
    /// left the attributes of generated version files for the SDK to duplicate (CS0579).
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR4306")]
    public async Task Shared_and_generated_assembly_info_files_stay_and_the_sdk_does_not_generate_their_attributes()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-shared");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var shared = repository.Directory.Read("src/SharedAssemblyInfo.cs");
        var buildInfo = repository.Directory.Read("src/Layers.Domain/Properties/BuildInfo.cs");

        // One project, as the guide's port step converts them.
        var single = await cli.RunAsync("csproj", "modernize", "--project", "Layers.Data", "--json");

        Assert.True(single.ExitCode == 0, single.Out);
        SchemaAssert.ValidEnvelope(single.Out, "csproj-modernize");
        var node = JsonNode.Parse(single.Out)!;
        var data = Assert.Single(node["result"]!["projects"]!.AsArray())!;
        Assert.True(data["verification"]!["passed"]!.GetValue<bool>(), single.Out);
        Assert.Equal(["src/Layers.Data/Properties/AssemblyInfo.cs"], data["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.DoesNotContain("a/src/SharedAssemblyInfo.cs", node["result"]!["preview"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(
            ["AssemblyTitle=Layers data access", "GenerateAssemblyCompanyAttribute=false", "GenerateAssemblyFileVersionAttribute=false", "GenerateAssemblyProductAttribute=false", "GenerateAssemblyVersionAttribute=false"],
            data["properties"]!.AsArray().Select(p => $"{p!["name"]}={p["value"]}").Order(StringComparer.Ordinal));
        var kept = Diagnostics(single.Out, "OFR4306");
        var linked = Assert.Single(kept, d => d["file"]?.GetValue<string>() == "src/SharedAssemblyInfo.cs");
        Assert.Equal(["src/Layers.Domain/Layers.Domain.csproj"], linked["data"]!["sharedWith"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(linked["data"]!["outsideProject"]!.GetValue<bool>());
        var version = Assert.Single(kept, d => d["file"]?.GetValue<string>() == "src/GlobalVersionInfo.cs");
        Assert.True(version["data"]!["addedByBuild"]!.GetValue<bool>());
        Assert.True(version["data"]!["ignoredByGit"]!.GetValue<bool>());

        // Every project, applied: the shared and generated files are as they were, and the conversions build.
        var applied = await cli.RunAsync("csproj", "modernize", "--all", "--apply", "--json");

        Assert.True(applied.ExitCode == 0, applied.Out);
        Assert.True(JsonNode.Parse(applied.Out)!["result"]!["applied"]!.GetValue<bool>(), applied.Out);
        Assert.Equal(shared, repository.Directory.Read("src/SharedAssemblyInfo.cs"));
        Assert.Equal(buildInfo, repository.Directory.Read("src/Layers.Domain/Properties/BuildInfo.cs"));
        Assert.DoesNotContain("AssemblyTitle", repository.Directory.Read("src/Layers.Domain/Properties/AssemblyInfo.cs"), StringComparison.Ordinal);
        var generated = Assert.Single(Diagnostics(applied.Out, "OFR4306"), d => d["file"]?.GetValue<string>() == "src/Layers.Domain/Properties/BuildInfo.cs");
        Assert.True(generated["data"]!["generatedCode"]!.GetValue<bool>());
        var domain = repository.Directory.Read("src/Layers.Domain/Layers.Domain.csproj");
        Assert.Contains("<GenerateAssemblyInformationalVersionAttribute>false</GenerateAssemblyInformationalVersionAttribute>", domain, StringComparison.Ordinal);
        Assert.Contains("<AssemblyTitle>Layers domain model</AssemblyTitle>", domain, StringComparison.Ordinal);
    }

    private static List<JsonNode> Diagnostics(string envelope, string code) =>
        [.. JsonNode.Parse(envelope)!["diagnostics"]!.AsArray().OfType<JsonNode>().Where(d => d["code"]!.GetValue<string>() == code)];

    [Fact]
    [ProducesDiagnostic("OFR4304")]
    public async Task Web_application_projects_are_not_converted()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            var project = Path.Combine(root, "src/Billing.Tool/Billing.Tool.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<OutputType>Exe</OutputType>",
                "<OutputType>Exe</OutputType>\n    <ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}</ProjectTypeGuids>", StringComparison.Ordinal));
            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("csproj", "modernize", "--project", "Billing.Tool", "--json");

        Assert.Equal(0, run.ExitCode);
        var project = JsonNode.Parse(run.Out)!["result"]!["projects"]![0]!;
        Assert.False(project["changed"]!.GetValue<bool>());
        Assert.Contains("web application", project["skipped"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("\"code\": \"OFR4304\"", run.Out, StringComparison.Ordinal);
    }
}
