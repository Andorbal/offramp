using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Offramp.Corpus.Tests.Harness;

namespace Offramp.Corpus.Tests.Codebases;

/// <summary>
/// Open Live Writer 0.6.3 (<c>docs/field-tests/2026-09-open-live-writer-0.6.3.md</c>): a WinForms desktop application
/// with heavy COM and P/Invoke interop, 28 legacy net461 C# projects and a native C++ project in one solution. Its own
/// build keeps most of it from compiling outside Windows, so the test asserts what Offramp names on the checkout as
/// shipped, applies the fixes those diagnostics prescribe, and then runs the sweep and pins the field test's findings.
/// </summary>
[Trait("Category", "Corpus")]
[Trait("Codebase", "olw")]
public sealed class OpenLiveWriterTests
{
    private const string Managed = "src/managed";

    [Fact(Timeout = 60 * 60 * 1000)]
    public async Task The_field_test_findings_stay_fixed()
    {
        await using var corpus = await CorpusRun.OpenAsync("olw");
        var repository = corpus.Repository;

        // Harness adjustment (network): NuGet.config names nuget.org's deprecated v2 endpoint, whose downloads come
        // from a CDN host some networks block; v3 is the endpoint nuget.org documents.
        repository.Write("NuGet.config", repository.Read("NuGet.config").Replace("https://nuget.org/api/v2/", "https://api.nuget.org/v3/index.json", StringComparison.Ordinal));

        // As shipped, after doctor --fix: writer.build.settings sets MSBuildExtensionsPath, so no project imports
        // Directory.Build.props and the compile-only block reaches none of them. Offramp says so, per project, and
        // counts the errors per project (P1 #5: "2 error(s)" and silence before).
        await corpus.RunAsync("doctor", "doctor", "--fix", "--apply", "--yes");
        var shipped = await corpus.RunAsync("scan", "scan");
        var unreached = shipped.Diagnostics("OFR0122").ToList();
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(unreached.Count >= 20, $"{unreached.Count} projects named as not reached by the compile-only block.");
            Assert.All(unreached, d => Assert.Contains("writer.build.settings", Message(d), StringComparison.Ordinal));
            Assert.True(Assert.Single(shipped.Diagnostics("OFR0130"))["data"]!["errorCount"]!.GetValue<int>() >= 25);
        }

        // P1 #8: the native project is named once and is not a .NET Framework library in the model.
        Assert.Contains(shipped.Diagnostics("OFR0024"), d => Project(d).EndsWith("OpenLiveWriter.Ribbon.vcxproj", StringComparison.Ordinal));

        // Harness adjustment, the fix OFR0122 names: set MSBuildExtensionsPath on Windows only.
        repository.Write("writer.build.settings", Regex.Replace(repository.Read("writer.build.settings"),
            "<(MSBuildExtensionsPath(?:32)?)>", "<$1 Condition=\"'$(OS)' == 'Windows_NT'\">", RegexOptions.None, TimeSpan.FromSeconds(1)));

        // With the block in reach, one scan names the rest (P1 #6: each of these took another scan before).
        var reached = await corpus.RunAsync("scan", "scan");
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(reached.Diagnostics("OFR0119").Count() >= 10, "Non-string resources were not named for every project.");
            Assert.Contains(reached.Diagnostics("OFR0125"), d => Project(d).EndsWith("OpenLiveWriter.UnitTest.csproj", StringComparison.Ordinal));

            // P1 #7: the application is not built because the solution makes it depend on the native project.
            Assert.Contains(reached.Diagnostics("OFR0101"), d => Project(d).EndsWith("OpenLiveWriter/OpenLiveWriter.csproj", StringComparison.Ordinal)
                && Message(d).Contains("OpenLiveWriter.Ribbon.vcxproj", StringComparison.Ordinal));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(reached.Diagnostics("OFR0117"), d => Strings(d["data"]?["paths"]).Any(p => p.EndsWith("intl/markets/Master.xml", StringComparison.Ordinal)));
        }

        // Harness adjustments, each the fix a diagnostic above prescribes or docs/compiling-on-macos.md gives for it:
        // - OFR0117: links for the paths in the wrong letter case (Linux only);
        // - OFR0119 and OFR0125: the documented Directory.Build.props sections for legacy projects;
        // - OFR0115 (a generator the solution builds): guard the target and supply its output, which the compile
        //   needs as an embedded resource (the generator itself runs on .NET Framework);
        // - OFR0115 (the installer's post-build event): no post-build events in compile-only builds;
        // - OFR0101 (P1 #7): the native project is not built in Debug, so the application it depends on is.
        CaseLinks.Apply(reached, repository.Path);
        if (!OperatingSystem.IsWindows())
        {
            var mstest = reached.Diagnostics("OFR0125").Select(d => Path.GetFileNameWithoutExtension(Project(d))).Order(StringComparer.Ordinal).ToList();
            repository.Write("Directory.Build.props", repository.Read("Directory.Build.props").Replace("</Project>", LegacySections(mstest) + "</Project>", StringComparison.Ordinal));
        }
        var coreServices = $"{Managed}/OpenLiveWriter.CoreServices/OpenLiveWriter.CoreServices.csproj";
        repository.Write(coreServices, repository.Read(coreServices).Replace(
            "<Target Name=\"GenerateMarketXmlImpl\"", "<Target Name=\"GenerateMarketXmlImpl\" Condition=\"'$(OfframpCompileOnly)' != 'true'\"", StringComparison.Ordinal));
        repository.Write($"{Managed}/OpenLiveWriter.CoreServices/Marketization/Markets.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<features />\n");
        repository.Write("offramp.yml", repository.Read("offramp.yml") + "  properties:\n    PostBuildEvent: \"\"\n");
        repository.Write($"{Managed}/writer.sln", string.Join('\n', repository.Read($"{Managed}/writer.sln").Split('\n')
            .Where(l => !l.Contains("{195A60BF-7A4D-42E6-B5F4-FEBC679E19F0}.Debug|Any CPU.Build.0", StringComparison.Ordinal))));

        var sweep = await corpus.SweepAsync();

        // scan: all 28 C# projects load and compile.
        Assert.Equal(28, sweep.Scan.Result["projects"]!.GetValue<int>());
        Assert.Empty(sweep.Scan.Result["notLoaded"]!.AsArray());
        Assert.Empty(sweep.Scan.Result["partial"]!.AsArray());

        // P1 #8: the MSTest v1 project is a test project (a Reference to the Visual Studio test framework).
        var model = JsonNode.Parse(repository.Read(".offramp/workspace.json"))!;
        Assert.Equal("test", model["projects"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "OpenLiveWriter.UnitTest")!["kind"]!.GetValue<string>());

        // P0 #1: WinForms class libraries are compiled for net10.0-windows. Windows Forms and System.Drawing are
        // not missing, the WinForms types .NET keeps only as throwing shims are named, and inherited Component
        // members are not blamed.
        var api = sweep.AuditApi!.Result["findings"]!.AsArray();
        var windowsForms = api.Count(f => Rule(f) == "OFR3001" && f!["details"]?["assembly"]?.GetValue<string>() is "System.Windows.Forms" or "System.Drawing");
        Assert.True(windowsForms < 100, $"{windowsForms} OFR3001 findings for Windows Forms and System.Drawing (13,374 before).");
        Assert.DoesNotContain(api, f => Rule(f) == "OFR3001" && Symbol(f).StartsWith("System.ComponentModel.Component.DesignMode", StringComparison.Ordinal));
        Assert.Contains(api, f => Symbol(f).StartsWith("System.Windows.Forms.MenuItem", StringComparison.Ordinal));
        Assert.True(api.Count(f => Rule(f) == "OFR3002") < 20);

        // P0 #2: the plugin SDK a .nuspec ships, and methods JavaScript calls on a COM-visible object, are not
        // high-confidence dead code.
        var high = sweep.DeadCode!.Result["projects"]!.AsArray()
            .SelectMany(p => p!["candidates"]!.AsArray())
            .Where(c => c!["confidence"]!.GetValue<string>() == "high")
            .Select(c => c!["symbol"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("OpenLiveWriter.Api.WriterApplication", high);
        Assert.DoesNotContain(high, s => s.StartsWith("OpenLiveWriter.InternalWriterPlugin.JSMapController.NextEvent", StringComparison.Ordinal));

        // P0 #4: no package version guessed for a DLL outside the repository.
        Assert.DoesNotContain(sweep.ResolveDlls.Diagnostics("OFR1402"), d => Message(d).Contains("  from package", StringComparison.Ordinal));
        Assert.DoesNotContain("Include=\"System.Resources.Extensions\" Version=\"4.6.0\"", sweep.ResolveDlls.Result["preview"]?.GetValue<string>() ?? "", StringComparison.Ordinal);

        // deps audit sees the packages.config packages, and a package whose code calls Windows DLLs is Windows-only.
        var packages = sweep.DepsAudit.Result["packages"]!.AsArray();
        Assert.True(packages.Count >= 15, $"{packages.Count} packages audited.");
        Assert.True(packages.Single(p => p!["id"]!.GetValue<string>() == "DeltaCompressionDotNet")!["windowsOnly"]!.GetValue<bool>());

        // P1 #9: csproj modernize no longer fails on a version file the build generates (CS0579 in 20 of 28).
        Assert.DoesNotContain(sweep.Modernize!.Diagnostics("OFR4303"), d => Message(d).Contains("CS0579", StringComparison.Ordinal));

        // P0 #3: converting one project, as the guide does, leaves GlobalAssemblyInfo.cs, which 20 projects compile.
        var one = await corpus.RunAsync("csproj-modernize", "csproj", "modernize",
            "--project", $"{Managed}/OpenLiveWriter.Localization/OpenLiveWriter.Localization.csproj", "--tfm", "net461;net10.0-windows");
        Assert.DoesNotContain($"{Managed}/GlobalAssemblyInfo.cs", one.Result["projects"]!.AsArray().SelectMany(p => Strings(p!["files"])));

        // P1 #10: the first move out of a .NET Framework project into a new netstandard2.0 project plans moves.
        var extract = await corpus.RunAsync("move-extract", "move", "extract", "--from", coreServices,
            "--new", "OpenLiveWriter.CoreServices.Portable", "--tfm", "netstandard2.0", "--files", $"{Managed}/OpenLiveWriter.CoreServices/Progress/*.cs");
        Assert.True(extract.Result["plan"]!["moves"]!.AsArray().Count >= 5, extract.Result["plan"]!["excluded"]?.ToJsonString());
    }

    /// <summary>The Directory.Build.props sections docs/compiling-on-macos.md gives for OFR0119 and OFR0125.</summary>
    private static string LegacySections(List<string> mstestProjects)
    {
        const string legacy = "'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true'";
        const string net461 = "$([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion.TrimStart('v')), '4.6.1'))";
        var resources = $"""
              <PropertyGroup Condition="{legacy}">
                <GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>
              </PropertyGroup>
              <ItemGroup Condition="{legacy} And {net461}">
                <PackageReference Include="System.Resources.Extensions" Version="6.0.0" IsImplicitlyDefined="true" PrivateAssets="all" />
              </ItemGroup>
              <Target Name="AddSystemResourcesExtensions" BeforeTargets="ResolveAssemblyReferences" Condition="{legacy} And {net461}">
                <ItemGroup>
                  <Reference Include="$(NuGetPackageRoot)system.resources.extensions/6.0.0/lib/net461/System.Resources.Extensions.dll" />
                </ItemGroup>
              </Target>

            """;
        if (mstestProjects.Count == 0)
        {
            return resources;
        }

        var mstest = string.Join(" Or ", mstestProjects.Select(p => $"'$(MSBuildProjectName)' == '{p}'"));
        return resources + $"""
              <ItemGroup Condition="{legacy} And ({mstest})">
                <PackageReference Include="MSTest.TestFramework" Version="1.4.0" IsImplicitlyDefined="true" PrivateAssets="all" />
              </ItemGroup>
              <Target Name="AddMSTestFramework" BeforeTargets="ResolveAssemblyReferences" Condition="{legacy} And ({mstest})">
                <ItemGroup>
                  <Reference Include="$(NuGetPackageRoot)mstest.testframework/1.4.0/lib/net45/Microsoft.VisualStudio.TestPlatform.TestFramework.dll" />
                  <Reference Include="$(NuGetPackageRoot)mstest.testframework/1.4.0/lib/net45/Microsoft.VisualStudio.TestPlatform.TestFramework.Extensions.dll" />
                </ItemGroup>
              </Target>

            """;
    }

    private static string Message(JsonNode diagnostic) => diagnostic["message"]!.GetValue<string>();

    private static string Project(JsonNode diagnostic) => diagnostic["project"]?.GetValue<string>() ?? "";

    private static string Rule(JsonNode? finding) => finding!["rule"]!.GetValue<string>();

    private static string Symbol(JsonNode? finding) => finding!["symbol"]?.GetValue<string>() ?? "";

    private static IEnumerable<string> Strings(JsonNode? array) => array?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
}
