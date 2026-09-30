using System.Text.Json.Nodes;
using Offramp.Corpus.Tests.Harness;

namespace Offramp.Corpus.Tests.Codebases;

/// <summary>
/// Open Live Writer 0.6.3 (<c>docs/field-tests/2026-09-open-live-writer-0.6.3.md</c>): a WinForms desktop application
/// with heavy COM and P/Invoke interop, 28 legacy net461 C# projects and a native C++ project in one solution. Its own
/// build keeps most of it from compiling outside Windows, so the test asserts what <c>doctor --fix</c> conditions and
/// what Offramp names after it, applies the fixes those diagnostics prescribe, and then runs the sweep and pins the
/// field test's findings.
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

        // doctor --fix conditions what the build files set for Windows only, where the compile-only block cannot reach
        // it: writer.build.settings points MSBuildExtensionsPath into the build tools "to prevent accidental pickup of
        // local-machine scripts", so no project imported Microsoft.Common.props, or Directory.Build.props with it (the
        // shipped checkout had OFR0122 in all 28 projects); an Exec runs MarketXmlGenerator.exe, which the solution
        // builds; and the installer's post-build event is written for cmd.exe.
        var doctor = await corpus.RunAsync("doctor", "doctor", "--fix", "--apply", "--yes");
        var guards = doctor.Result["fix"]!["projectFiles"]!.AsArray()
            .SelectMany(f => f!["guards"]!.AsArray().Select(g => $"{f["file"]!.GetValue<string>()} {g!["setting"]!.GetValue<string>()}"))
            .ToList();
        Assert.Contains("writer.build.settings MSBuildExtensionsPath", guards);
        Assert.Contains("writer.build.settings MSBuildExtensionsPath32", guards);
        Assert.Contains($"{Managed}/OpenLiveWriter.CoreServices/OpenLiveWriter.CoreServices.csproj Exec in target GenerateMarketXmlImpl", guards);
        Assert.Contains($"{Managed}/PostBuild.CreateInstaller/PostBuild.CreateInstaller.csproj PostBuildEvent", guards);

        // With the block in reach of every project, one scan names the rest (P1 #6: each of these took another scan
        // before), and counts the errors per project (P1 #5: "2 error(s)" and silence before).
        var reached = await corpus.RunAsync("scan", "scan");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(reached.Diagnostics("OFR0122"));
            Assert.True(Assert.Single(reached.Diagnostics("OFR0130"))["data"]!["errorCount"]!.GetValue<int>() > 0);
            Assert.True(reached.Diagnostics("OFR0119").Count() >= 10, "Non-string resources were not named for every project.");
            Assert.Contains(reached.Diagnostics("OFR0125"), d => Project(d).EndsWith("OpenLiveWriter.UnitTest.csproj", StringComparison.Ordinal));

            // P1 #7: a project MSBuild skipped names the reference that failed, not "the solution configuration".
            Assert.DoesNotContain(reached.Diagnostics("OFR0101"), d => Message(d).Contains("solution configuration", StringComparison.Ordinal));
        }

        // P1 #8: the native project is named once and is not a .NET Framework library in the model.
        Assert.Contains(reached.Diagnostics("OFR0024"), d => Project(d).EndsWith("OpenLiveWriter.Ribbon.vcxproj", StringComparison.Ordinal));

        // Harness adjustments, each the fix a diagnostic above prescribes or docs/compiling-on-macos.md gives for it:
        // - OFR0117: links for the paths in the wrong letter case (Linux only);
        // - OFR0119 and OFR0125: the documented Directory.Build.props sections for legacy projects;
        // - OFR0115 (a generator the solution builds, whose Exec doctor --fix conditioned): supply its output, which
        //   the compile needs as an embedded resource (the generator itself runs on .NET Framework);
        // - OFR0101 (P1 #7): the native project is not built in Debug, so the application it depends on is (the
        //   regular suite pins that OFR0101 names the native project).
        CaseLinks.Apply(reached, repository.Path);
        if (!OperatingSystem.IsWindows())
        {
            var mstest = reached.Diagnostics("OFR0125").Select(d => Path.GetFileNameWithoutExtension(Project(d))).Order(StringComparer.Ordinal).ToList();
            repository.Write("Directory.Build.props", repository.Read("Directory.Build.props").Replace("</Project>", LegacySections(mstest) + "</Project>", StringComparison.Ordinal));
        }

        repository.Write($"{Managed}/OpenLiveWriter.CoreServices/Marketization/Markets.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<features />\n");
        repository.Write($"{Managed}/writer.sln", string.Join('\n', repository.Read($"{Managed}/writer.sln").Split('\n')
            .Where(l => !l.Contains("{195A60BF-7A4D-42E6-B5F4-FEBC679E19F0}.Debug|Any CPU.Build.0", StringComparison.Ordinal))));

        // Now the build reaches OpenLiveWriter.CoreServices, whose target copies intl/markets/Master.xml in another
        // letter case (a path only the build's error shows); link it as OFR0117 says, as a user would after this scan.
        var building = await corpus.RunAsync("scan", "scan");
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(building.Diagnostics("OFR0117"), d => Strings(d["data"]?["paths"]).Any(p => p.EndsWith("intl/markets/Master.xml", StringComparison.Ordinal)));
            CaseLinks.Apply(building, repository.Path);
        }

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

        // P1 #9: every conversion verifies (0 of 28 before) except the two MSTest v1 test projects, which lose the
        // harness's legacy-only MSTest references once they are SDK-style (still open: csproj modernize could move them
        // to MSTest.TestFramework, as docs/compiling-on-macos.md recommends).
        Assert.All(sweep.Modernize!.Diagnostics("OFR4303"), d => Assert.Contains(Path.GetFileNameWithoutExtension(Project(d)), (string[])["OpenLiveWriter.Tests", "OpenLiveWriter.UnitTest"]));

        // P0 #3: converting one project, as the guide does, leaves GlobalAssemblyInfo.cs, which 20 projects compile.
        var one = await corpus.RunAsync("csproj-modernize", "csproj", "modernize",
            "--project", $"{Managed}/OpenLiveWriter.Localization/OpenLiveWriter.Localization.csproj", "--tfm", "net461;net10.0-windows");
        Assert.DoesNotContain($"{Managed}/GlobalAssemblyInfo.cs", one.Result["projects"]!.AsArray().SelectMany(p => Strings(p!["files"])));
        Assert.Empty(one.Diagnostics("OFR4303"));

        // P1 #10: the first move out of a .NET Framework project into a new netstandard2.0 project plans moves.
        var extract = await corpus.RunAsync("move-extract", "move", "extract", "--from", $"{Managed}/OpenLiveWriter.CoreServices/OpenLiveWriter.CoreServices.csproj",
            "--new", "OpenLiveWriter.CoreServices.Portable", "--tfm", "netstandard2.0", "--files", $"{Managed}/OpenLiveWriter.CoreServices/Progress/*.cs");
        Assert.True(extract.Result["plan"]!["moves"]!.AsArray().Count >= 5, extract.Result["plan"]!["excluded"]?.ToJsonString());
    }

    /// <summary>The Directory.Build.props sections docs/compiling-on-macos.md gives for OFR0119 (legacy and SDK-style) and OFR0125.</summary>
    private static string LegacySections(List<string> mstestProjects)
    {
        const string legacy = "'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true'";
        const string net461 = "$([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion.TrimStart('v')), '4.6.1'))";

        // The SDK-style form, for the projects csproj modernize converts (an item condition sees TargetFramework).
        const string sdk = "'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' == 'true'";
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
              <PropertyGroup Condition="{sdk}">
                <GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>
              </PropertyGroup>
              <ItemGroup Condition="{sdk} And $(TargetFramework.StartsWith('net4'))">
                <PackageReference Include="System.Resources.Extensions" Version="6.0.0" />
              </ItemGroup>

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
