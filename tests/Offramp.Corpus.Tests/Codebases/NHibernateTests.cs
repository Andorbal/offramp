using System.Text.Json.Nodes;
using Offramp.Corpus.Tests.Harness;

namespace Offramp.Corpus.Tests.Codebases;

/// <summary>
/// NHibernate 4.1.2 (<c>docs/field-tests/2026-09-nhibernate-4.1.2.md</c>): an object-relational mapper shipped on
/// NuGet, 5 legacy .NET Framework 4.0 projects with DLLs checked in under <c>lib/</c>, one in Visual Basic, built by
/// NAnt. The assertions pin what that field test found wrong about a library; the sweep checks the rest.
/// </summary>
[Trait("Category", "Corpus")]
[Trait("Codebase", "nhibernate")]
public sealed class NHibernateTests
{
    private const string Library = "src/NHibernate/NHibernate.csproj";

    [Fact(Timeout = 45 * 60 * 1000)]
    public async Task The_field_test_findings_stay_fixed()
    {
        await using var corpus = await CorpusRun.OpenAsync("nhibernate");

        // A fresh checkout: the NAnt build writes src/SharedAssemblyInfo.cs, which is git-ignored and linked by
        // every project. Offramp names the missing file and why before the build fails on it (P1 #14), and marks
        // the library partial, since its compiler call failed.
        await corpus.RunAsync("doctor", "doctor", "--fix", "--apply", "--yes");
        var fresh = await corpus.RunAsync("scan", "scan");
        var missing = fresh.Diagnostics("OFR0123").Where(d => Data(d, "file") == "src/SharedAssemblyInfo.cs").ToList();
        Assert.True(missing.Count >= 4, $"{missing.Count} projects named the missing src/SharedAssemblyInfo.cs.");
        Assert.All(missing, d => Assert.Contains("src/SharedAssemblyInfo.cs", Strings(d["data"]!["gitIgnored"])));
        Assert.Contains(Library, Strings(fresh.Result["partial"]));

        // Harness adjustment: what CONTRIBUTING.md has every contributor do first (NAnt's "build menu, option A"),
        // with the values build-common/common.xml gives 4.1.2.GA.
        corpus.Repository.Write("src/SharedAssemblyInfo.cs",
            "using System.Reflection;\n\n[assembly: AssemblyVersion(\"4.1.0.4000\")]\n[assembly: AssemblyInformationalVersion(\"4.1.2.GA\")]\n[assembly: AssemblyFileVersion(\"4.1.2.4000\")]\n");
        corpus.Repository.Write("src/SharedAssemblyInfo.vb",
            "Imports System.Reflection\n\n<Assembly: AssemblyVersion(\"4.1.0.4000\")>\n<Assembly: AssemblyInformationalVersion(\"4.1.2.GA\")>\n<Assembly: AssemblyFileVersion(\"4.1.2.4000\")>\n");

        var sweep = await corpus.SweepAsync();

        // scan: every project builds outside Windows, the Visual Basic one included, with nothing but the
        // compile-only block.
        Assert.Equal(5, sweep.Scan.Result["projects"]!.GetValue<int>());
        Assert.Empty(sweep.Scan.Result["notLoaded"]!.AsArray());
        Assert.Empty(sweep.Scan.Result["partial"]!.AsArray());

        // P0 #2: the test projects reference nunit.framework by HintPath, and are test projects.
        Assert.True(sweep.Scan.Result["byKind"]!["test"]!.GetValue<int>() >= 3, sweep.Scan.Result["byKind"]!.ToJsonString());

        // P0 #1 and #2: no public NHibernate API at high confidence (a .nuspec.template packs it), and the NUnit
        // fixtures are tests, not dead code.
        var dead = sweep.DeadCode!.Result["projects"]!.AsArray();
        Assert.DoesNotContain(dead, p => p!["project"]!.GetValue<string>() == "src/NHibernate.Test/NHibernate.Test.csproj");
        var core = dead.Single(p => p!["project"]!.GetValue<string>() == Library)!;
        Assert.DoesNotContain(core["candidates"]!.AsArray(), c => c!["accessibility"]?.GetValue<string>() == "public" && c["confidence"]!.GetValue<string>() == "high");

        // P0 #4: the checked-in Iesi.Collections is byte for byte 4.0.1.4000, not the lowest version with its
        // assembly version.
        var iesi = sweep.ResolveDlls.Result["projects"]!.AsArray()
            .SelectMany(p => p!["references"]!.AsArray())
            .First(r => r!["name"]!.GetValue<string>() == "Iesi.Collections")!;
        Assert.Equal("4.0.1.4000", iesi["resolution"]!["version"]?.GetValue<string>());

        // P0 #5, P1 #11, P2: csproj modernize leaves the generated shared file alone, keeps the compile set of a
        // project with a transitive reference, and names the Visual Basic project it does not convert.
        var modernized = sweep.Modernize!.Result["projects"]!.AsArray();
        Assert.All(modernized, p => Assert.DoesNotContain("src/SharedAssemblyInfo.cs", Strings(p!["files"])));
        Assert.Contains("OFR4306", sweep.Modernize.Codes);
        Assert.DoesNotContain(sweep.Modernize.Diagnostics("OFR4303"), d => Project(d).EndsWith("NHibernate.TestDatabaseSetup.csproj", StringComparison.Ordinal));
        Assert.Contains(sweep.Modernize.Diagnostics("OFR4304"), d => Project(d).EndsWith(".vbproj", StringComparison.Ordinal));

        // P1 #8: transparency attributes are harmless on modern .NET, and CallContext has its own advice.
        var api = sweep.AuditApi!.Result["findings"]!.AsArray();
        Assert.DoesNotContain(api, f => Rule(f) == "OFR3009" && Symbol(f).Contains("SecurityCriticalAttribute", StringComparison.Ordinal));
        Assert.Contains(api, f => Rule(f) == "OFR3013");
        Assert.DoesNotContain(api, f => Rule(f) == "OFR3007" && Symbol(f).Contains("CallContext", StringComparison.Ordinal));

        // P0 #3 and the DotNetNuke still-open items: move tests finds NHibernate.Test (it is not named
        // NHibernate.Tests) and never moves the public QueryOver API into it.
        var tests = await corpus.RunAsync("move-tests", "move", "tests", "--project", Library);
        Assert.Contains("OFR2207", tests.Codes);
        Assert.DoesNotContain(tests.Result["moves"]!.AsArray(), m => m!["file"]!.GetValue<string>().EndsWith("QueryOverBuilderExtensions.cs", StringComparison.Ordinal));

        // P1 #9: calls do not join a cycle, so seams fences a few types instead of 62% of the library.
        var seams = await corpus.RunAsync("seams", "seams", "--project", Library);
        var types = seams.Result["types"]!.AsArray().Count;
        Assert.True(seams.Result["tainted"]!.AsArray().Count < types / 4, $"{seams.Result["tainted"]!.AsArray().Count} of {types} types tainted.");
    }

    private static string? Data(JsonNode diagnostic, string key) => diagnostic["data"]?[key]?.GetValue<string>();

    private static string Project(JsonNode diagnostic) => diagnostic["project"]?.GetValue<string>() ?? "";

    private static string Rule(JsonNode? finding) => finding!["rule"]!.GetValue<string>();

    private static string Symbol(JsonNode? finding) => finding!["symbol"]?.GetValue<string>() ?? "";

    private static IEnumerable<string> Strings(JsonNode? array) => array?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
}
