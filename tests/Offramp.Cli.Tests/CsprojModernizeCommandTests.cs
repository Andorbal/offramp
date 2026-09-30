using System.Text.Json.Nodes;
using Offramp.Cli.Commands;
using Offramp.Cli.Rendering;
using Offramp.Fixtures;
using Offramp.Scaffolding.Csproj;

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
        var verification = node["result"]!["projects"]![0]!["verification"]!;
        Assert.NotEmpty(verification["buildErrors"]!.AsArray());
        Assert.Contains(verification["buildErrors"]!.AsArray(), e => e!.GetValue<string>().StartsWith("src/Billing/BillingSection.cs(", StringComparison.Ordinal));

        // NHibernate 4.1.2 (P1 #7): the error count by code, not only the first errors.
        Assert.False(verification["built"]!.GetValue<bool>());
        var count = verification["buildErrorCount"]!.GetValue<int>();
        var codes = verification["buildErrorCodes"]!.AsArray();
        Assert.Contains(codes, c => c!["code"]!.GetValue<string>().StartsWith("CS", StringComparison.Ordinal));
        Assert.Equal(count, codes.Sum(c => c!["count"]!.GetValue<int>()));
        Assert.Contains($"{count} error", failure["message"]!.GetValue<string>(), StringComparison.Ordinal);
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
        Assert.Equal(["src/Layers.Data/Properties/AssemblyInfo.cs", "src/Layers.Data/packages.config"], data["files"]!.AsArray().Select(f => f!.GetValue<string>()));
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

    /// <summary>
    /// NHibernate 4.1.2 (P1 #11), SmartStoreNET 4.2.0 (P1 #6), Open Live Writer 0.6.3 (P1 #9): conversions
    /// that failed verification because of what the SDK passes on or dropped (transitive project
    /// references, the NuGet 2 restore import, a package downgrade), and a build event that lost its condition.
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR4307")]
    [ProducesDiagnostic("OFR4308")]
    public async Task Conversions_compile_what_the_legacy_projects_did()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-shared");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("csproj", "modernize", "--all", "--apply", "--json");

        Assert.True(run.ExitCode == 0, run.Out);
        SchemaAssert.ValidEnvelope(run.Out, "csproj-modernize");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal(3, result["projects"]!.AsArray().Count);
        Assert.All(result["projects"]!.AsArray(), p => Assert.True(p!["verification"]!["passed"]!.GetValue<bool>(), p.ToJsonString()));
        Assert.True(result["applied"]!.GetValue<bool>());
        List<string> Lines(string project) => [.. repository.Directory.Read(project).Split('\n').Select(l => l.Trim())];
        var tests = Lines("src/Layers.Tests/Layers.Tests.csproj");
        var data = Lines("src/Layers.Data/Layers.Data.csproj");
        var domain = Lines("src/Layers.Domain/Layers.Domain.csproj");

        // Layers.Tests → Layers.Data → Layers.Domain: Tests still compiles against Data alone.
        Assert.Contains("<DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>", tests);
        Assert.DoesNotContain("<DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>", data);
        // NuGet 2's restore import goes; SolutionDir stays in Tests only, whose build event uses it.
        Assert.DoesNotContain(data, l => l.Contains("NuGet.targets", StringComparison.OrdinalIgnoreCase) || l.Contains("SolutionDir", StringComparison.Ordinal));
        Assert.Contains(tests, l => l.StartsWith("<SolutionDir Condition=", StringComparison.Ordinal));
        Assert.Contains("<PostBuildEvent Condition=\"'$(PostBuildEvent)' != '' and ('$(Configuration)' == 'Debug')\">echo Checks built for $(SolutionDir)</PostBuildEvent>", tests);
        // Data's Newtonsoft.Json 12.0.1 would be downgraded from Domain's 13.0.3.
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", data);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", domain);
        var raised = Assert.Single(Diagnostics(run.Out, "OFR4307"));
        Assert.Equal("src/Layers.Data/Layers.Data.csproj", raised["project"]!.GetValue<string>());
        Assert.Equal(("12.0.1", "13.0.3", "src/Layers.Domain/Layers.Domain.csproj"),
            (raised["data"]!["from"]!.GetValue<string>(), raised["data"]!["to"]!.GetValue<string>(), raised["data"]!["source"]!.GetValue<string>()));
        Assert.Equal(["src/Layers.Domain/Layers.Domain.csproj", "src/Layers.Tests/Layers.Tests.csproj"],
            Diagnostics(run.Out, "OFR4308").Select(d => d["project"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// SmartStoreNET 4.2.0 (corpus): SmartStore.Data.Tests' post-build step (cmd's <c>md</c> and <c>xcopy</c>)
    /// became a target with the command inline, so <c>verify.properties: PostBuildEvent: ""</c> (OFR0115's
    /// remedy) no longer turned it off and verification failed with MSB3073.
    /// </summary>
    [Fact]
    public async Task A_converted_build_event_is_turned_off_as_the_legacy_one_was()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-shared", (root, request) =>
        {
            var tests = Path.Combine(root, "src/Layers.Tests/Layers.Tests.csproj");
            File.WriteAllText(tests, File.ReadAllText(tests).Replace("echo Checks built for $(SolutionDir)", "exit 3", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(root, "offramp.yml"), "version: 1\nverify:\n  properties:\n    PostBuildEvent: \"\"\n");
            return request with { Config = request.Config with { Verify = request.Config.Verify with { Properties = new(StringComparer.Ordinal) { ["PostBuildEvent"] = "" } } } };
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("csproj", "modernize", "--project", "Layers.Tests", "--json");

        Assert.True(run.ExitCode == 0, run.Out);
        var project = Assert.Single(JsonNode.Parse(run.Out)!["result"]!["projects"]!.AsArray())!;
        Assert.True(project["verification"]!["passed"]!.GetValue<bool>(), run.Out);
        Assert.Contains("<PostBuildEvent Condition=\"'$(PostBuildEvent)' != '' and ('$(Configuration)' == 'Debug')\">exit 3</PostBuildEvent>", JsonNode.Parse(run.Out)!["result"]!["preview"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// SmartStoreNET 4.2.0 (corpus): the legacy projects import <c>$(SolutionDir)\.nuget\nuget.targets</c>, which on
    /// Linux exists only as an untracked link to <c>NuGet.targets</c> in the working tree (OFR0117's fix). The
    /// scratch copy verification builds in lacked it, so a converted project's legacy reference failed with MSB4019.
    /// </summary>
    [Fact]
    public async Task Verification_builds_with_the_working_trees_untracked_imports()
    {
        var fixture = await ScannedFixtures.ScanAsync("legacy-shared", (root, request) =>
        {
            var data = Path.Combine(root, "src/Layers.Data/Layers.Data.csproj");
            File.WriteAllText(data, File.ReadAllText(data).Replace(@"\.nuget\NuGet.targets", @"\.nuget\nuget.targets", StringComparison.Ordinal));
            var link = Path.Combine(root, ".nuget", "nuget.targets");
            if (!File.Exists(link))
            {
                File.CreateSymbolicLink(link, "NuGet.targets");
            }

            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("csproj", "modernize", "--project", "Layers.Tests", "--json");

        Assert.True(run.ExitCode == 0, run.Out);
        Assert.True(JsonNode.Parse(run.Out)!["result"]!["projects"]![0]!["verification"]!["passed"]!.GetValue<bool>(), run.Out);
    }

    private static List<JsonNode> Diagnostics(string envelope, string code) =>
        [.. JsonNode.Parse(envelope)!["diagnostics"]!.AsArray().OfType<JsonNode>().Where(d => d["code"]!.GetValue<string>() == code)];

    /// <summary>NHibernate 4.1.2 (P2): <c>--all</c> left the Visual Basic project out without a word.</summary>
    [Fact]
    public async Task All_names_the_legacy_projects_it_does_not_convert()
    {
        var fixture = await ScannedFixtures.GetAsync("webforms");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("csproj", "modernize", "--all", "--json");

        Assert.True(run.ExitCode == 0, run.Out);
        SchemaAssert.ValidEnvelope(run.Out, "csproj-modernize");
        var vb = Assert.Single(JsonNode.Parse(run.Out)!["result"]!["projects"]!.AsArray(), p => p!["project"]!.GetValue<string>() == "src/Portal.Utilities/Portal.Utilities.vbproj")!;
        Assert.Contains("vb project", vb["skipped"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(Diagnostics(run.Out, "OFR4304"), d => d["project"]!.GetValue<string>() == "src/Portal.Utilities/Portal.Utilities.vbproj");
    }

    /// <summary>SmartStoreNET 4.2.0 (P2): the terminal said "compiles different inputs" for a conversion that did not build.</summary>
    [Fact]
    public void The_terminal_view_tells_a_failed_build_from_a_different_compile_set()
    {
        using var cli = new CliHarness();
        var writer = new StringWriter { NewLine = "\n" };
        var output = new HumanOutput(ConsoleFactory.Create(cli.Host(writer, TextWriter.Null), writer, isTerminal: false));
        var result = new ModernizeResult
        {
            Projects =
            [
                new ModernizedProject
                {
                    Project = "src/Broken/Broken.csproj", Style = "legacy", Changed = true, TargetFrameworks = ["net48"],
                    Verification = new ModernizeVerification
                    {
                        Passed = false, Built = false, BuildErrorCount = 71,
                        BuildErrorCodes = [new BuildErrorCode("CS0246", 50), new BuildErrorCode("CS0234", 21)],
                        BuildErrors = ["src/Broken/Emit.cs(12,5): error CS0246: The type or namespace name 'ILGenerator' could not be found"],
                    },
                },
                new ModernizedProject
                {
                    Project = "src/Different/Different.csproj", Style = "legacy", Changed = true, TargetFrameworks = ["net48"],
                    Verification = new ModernizeVerification
                    {
                        Passed = false, Built = true,
                        Targets = [new CompileSetDifference { TargetFramework = "net48", ReferencesAdded = ["NHibernate.DomainModel"] }],
                    },
                },
            ],
        };

        new CsprojModernizeCommand().Render(result, null!, output);

        var lines = writer.ToString().Split('\n');
        Assert.Contains("1 does not build; 1 compiles different inputs", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("src/Broken/Broken.csproj", StringComparison.Ordinal) && l.Contains("does not build", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("src/Broken/Broken.csproj", StringComparison.Ordinal) && l.Contains("different compile set", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("build errors: 71 (50 CS0246, 21 CS0234)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("src/Different/Different.csproj", StringComparison.Ordinal) && l.Contains("different compile set", StringComparison.Ordinal));
    }

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
