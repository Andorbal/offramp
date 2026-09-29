using Offramp.Corpus.Tests.Harness;

namespace Offramp.Corpus.Tests.Codebases;

/// <summary>
/// DotNetNuke Platform 9.13.10 (<c>docs/field-tests/2026-09-dnn-platform-9.13.10.md</c>): a Web Forms CMS with
/// 71 projects, 64 of them legacy and on packages.config, one in Visual Basic. The assertions pin what that field
/// test found wrong; the sweep checks the rest (see README.md).
/// </summary>
[Trait("Category", "Corpus")]
[Trait("Codebase", "dnn")]
public sealed class DotNetNukeTests
{
    [Fact(Timeout = 100 * 60 * 1000)]
    public async Task The_field_test_findings_stay_fixed()
    {
        await using var corpus = await CorpusRun.OpenAsync("dnn");

        // Harness adjustment: DotNetNuke pins SDK 9.0.202 with latestMinor; let the SDK running the tests build it.
        corpus.Repository.Write("global.json", "{ \"sdk\": { \"version\": \"9.0.100\", \"rollForward\": \"latestMajor\" } }\n");

        var sweep = await corpus.SweepAsync();

        // doctor: no central package management, so no OFR1303 for the 64 packages.config projects.
        Assert.DoesNotContain("OFR1303", sweep.Doctor.Codes);

        // scan: Offramp supplies reference assemblies, web targets, and packages.config packages. The build still
        // stops at DotNetNuke's own problems outside Windows (XCOPY targets, and on Linux letter case), each named,
        // and projects MSBuild then skips name the reference that failed.
        var notLoaded = sweep.Scan.Result["notLoaded"]!.AsArray();
        Assert.Equal(71, sweep.Scan.Result["projects"]!.GetValue<int>() + notLoaded.Count);
        Assert.All(notLoaded, n => Assert.DoesNotContain("no evaluation", n!["reason"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Contains("OFR0106", sweep.Scan.Codes);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(sweep.Scan.Diagnostics("OFR0115"), d => Project(d).EndsWith("DotNetNuke.Abstractions.csproj", StringComparison.Ordinal));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("OFR0117", sweep.Scan.Codes);
        }

        // plan: a netstandard2.0 project referencing a net472 legacy project is blocked, not done (P0 #5).
        Assert.Contains(sweep.Scan.Diagnostics("OFR0121"), d => Project(d).EndsWith("DotNetNuke.DependencyInjection.csproj", StringComparison.Ordinal));
        var injection = sweep.Plan.Result["order"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "DotNetNuke.DependencyInjection")!;
        Assert.Equal("blocked", injection["readiness"]!.GetValue<string>());

        // deps resolve-dlls: packages.config DLLs are that package, not a guess from the assembly version (P0 #2).
        var dlls = sweep.ResolveDlls.Result["summary"]!;
        Assert.True(dlls["packagesConfig"]!.GetValue<int>() > 250, dlls.ToJsonString());
        Assert.True(dlls["blockers"]!.GetValue<int>() < 20, dlls.ToJsonString());

        // redirects sync --prune: no live redirect of a packages.config package goes, and applications whose
        // model is partial are left alone (P0 #3).
        var pruned = sweep.Redirects.Result["apps"]!.AsArray()
            .SelectMany(a => a!["redirects"]!.AsArray())
            .Where(r => r!["action"]!.GetValue<string>() == "pruned")
            .Select(r => r!["assembly"]!.GetValue<string>())
            .ToList();
        Assert.DoesNotContain("Newtonsoft.Json", pruned);
        Assert.DoesNotContain("BouncyCastle.Crypto", pruned);

        // deps audit sees the packages.config packages: 23 before the fix, 77 after (P1 #7).
        Assert.True(sweep.DepsAudit.Result["packages"]!.AsArray().Count >= 70, sweep.DepsAudit.Result["summary"]!.ToJsonString());

        // audit api: types that exist on the target are not blamed for a missing base type (P0 #1).
        var missing = sweep.AuditApi!.Result["findings"]!.AsArray()
            .Where(f => f!["rule"]!.GetValue<string>() == "OFR3001")
            .Select(f => f!["symbol"]!.GetValue<string>())
            .ToList();
        Assert.DoesNotContain(missing, s => s.StartsWith("System.Convert", StringComparison.Ordinal) || s.StartsWith("System.Exception", StringComparison.Ordinal));
    }

    private static string Project(System.Text.Json.Nodes.JsonNode diagnostic) => diagnostic["project"]?.GetValue<string>() ?? "";
}
