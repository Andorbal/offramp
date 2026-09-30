using System.Text.Json.Nodes;
using Offramp.Corpus.Tests.Harness;

namespace Offramp.Corpus.Tests.Codebases;

/// <summary>
/// SmartStoreNET 4.2.0 (<c>docs/field-tests/2026-09-smartstorenet-4.2.0.md</c>): an ASP.NET MVC 5 e-commerce
/// application with 12 plugins that build into the site, 25 legacy net472 projects on packages.config. It stands in
/// for the MVC 5 plugin hosts (nopCommerce 3.90, which it was forked from, Orchard 1.x, Umbraco 8). The assertions pin
/// what that field test found wrong; the sweep checks the rest.
/// </summary>
[Trait("Category", "Corpus")]
[Trait("Codebase", "smartstore")]
public sealed class SmartStoreNetTests
{
    private const string Site = "src/Presentation/SmartStore.Web/SmartStore.Web.csproj";

    [Fact(Timeout = 60 * 60 * 1000)]
    public async Task The_field_test_findings_stay_fixed()
    {
        await using var corpus = await CorpusRun.OpenAsync("smartstore");

        // A fresh checkout, after doctor --fix: Offramp restores packages.config and names SmartStoreNET's own
        // problems outside Windows, all in one scan (P1 #7: the letter-case problems took four scans).
        await corpus.RunAsync("doctor", "doctor", "--fix", "--apply", "--yes");
        var fresh = await corpus.RunAsync("scan", "scan");
        Assert.Contains("OFR0106", fresh.Codes);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(fresh.Diagnostics("OFR0115"), d => Project(d).EndsWith("SmartStore.DevTools.csproj", StringComparison.Ordinal));
        }

        if (OperatingSystem.IsLinux())
        {
            var importers = fresh.Diagnostics("OFR0117").Count(d => Strings(d["data"]?["paths"]).Any(p => p.EndsWith(".nuget/nuget.targets", StringComparison.Ordinal)));
            Assert.True(importers >= 19, $"{importers} of the 19 projects importing .nuget/nuget.targets were named.");
        }

        // Harness adjustments, each the fix a diagnostic above prescribes: links for the paths in the wrong letter
        // case (OFR0117, Linux only), and no cmd.exe post-build events (OFR0115).
        var linked = CaseLinks.Apply(fresh, corpus.Repository.Path);
        Assert.True(!OperatingSystem.IsLinux() || linked.Count >= 14, $"{linked.Count} paths linked.");
        corpus.Repository.Write("offramp.yml", corpus.Repository.Read("offramp.yml") + "  properties:\n    PostBuildEvent: \"\"\n");

        var sweep = await corpus.SweepAsync();

        // scan: every project loads and compiles; Microsoft.Bcl.Build's task is switched off by the compile-only
        // block (P1 #10). The sweep also checked that two full scans write the same model (P1 #8).
        Assert.Equal(25, sweep.Scan.Result["projects"]!.GetValue<int>());
        Assert.Empty(sweep.Scan.Result["notLoaded"]!.AsArray());
        Assert.Empty(sweep.Scan.Result["partial"]!.AsArray());

        // P1 #5: one site, not 16 applications; plan --for the site includes the plugins it loads.
        Assert.InRange(sweep.Report.Result["report"]!["headline"]!["applications"]!.GetValue<int>(), 1, 3);
        var plan = await corpus.RunAsync("plan", "plan", "--for", Site);
        Assert.Contains(plan.Result["order"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "SmartStore.Tax");

        // P0 #4: no version without assemblies proposed as an upgrade, and native Windows packages are Windows-only.
        var packages = sweep.DepsAudit.Result["packages"]!.AsArray();
        Assert.True(packages.Count >= 90, $"{packages.Count} packages audited.");
        Assert.NotEqual("4.3.1", Package(packages, "EntityFramework.SqlServerCompact")["newestSupporting"]?.GetValue<string>());
        Assert.True(Package(packages, "LibSassHost.Native.win-x64")["windowsOnly"]!.GetValue<bool>());

        // deps resolve-dlls leaves the packages.config DLLs alone (DotNetNuke P0 #2 holds here).
        var dlls = sweep.ResolveDlls.Result["summary"]!;
        Assert.True(dlls["packagesConfig"]!.GetValue<int>() >= 400, dlls.ToJsonString());

        // redirects sync keeps the site's live package redirects.
        var pruned = sweep.Redirects.Result["apps"]!.AsArray()
            .Where(a => a!["project"]!.GetValue<string>() == Site)
            .SelectMany(a => a!["redirects"]!.AsArray())
            .Where(r => r!["action"]!.GetValue<string>() == "pruned")
            .Select(r => r!["assembly"]!.GetValue<string>())
            .ToList();
        Assert.DoesNotContain("Newtonsoft.Json", pruned);
        Assert.DoesNotContain("Autofac", pruned);

        // P0 #2: ASP.NET MVC and Web API from packages.config are missing on the target.
        var missing = sweep.AuditApi!.Result["findings"]!.AsArray()
            .Where(f => f!["rule"]!.GetValue<string>() == "OFR3001")
            .Select(f => f!["symbol"]!.GetValue<string>())
            .ToList();
        Assert.Contains(missing, s => s.StartsWith("System.Web.Mvc.Controller", StringComparison.Ordinal));
        Assert.Contains(missing, s => s.StartsWith("System.Web.Http.ApiController", StringComparison.Ordinal));
        Assert.True(missing.Count > 5000, $"{missing.Count} OFR3001 findings.");
        Assert.DoesNotContain(missing, s => s.StartsWith("System.Convert", StringComparison.Ordinal));

        // P0 #3: classes the type finder discovers are not high-confidence dead code.
        var high = sweep.DeadCode!.Result["projects"]!.AsArray()
            .SelectMany(p => p!["candidates"]!.AsArray())
            .Where(c => c!["confidence"]!.GetValue<string>() == "high")
            .Select(c => c!["symbol"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("SmartStore.Web.Framework.DependencyRegistrar", high);
        Assert.DoesNotContain("SmartStore.Data.Mapping.Catalog.ProductMap", high);
        Assert.DoesNotContain(high, s => s.StartsWith("SmartStore.Web.Controllers.BoardsController.ActiveDiscussionsRss", StringComparison.Ordinal));

        // P0 #1 and P1 #6: csproj modernize leaves the shared assembly-info files alone, and no converted project
        // keeps the NuGet 2 restore import after dropping SolutionDir (MSB4019 on "/.nuget/nuget.targets" in 10 of 11).
        Assert.DoesNotContain(sweep.Modernize!.Result["projects"]!.AsArray().SelectMany(p => Strings(p!["files"])), f => f is "src/AssemblyVersionInfo.cs" or "src/AssemblySharedInfo.cs");
        Assert.DoesNotContain(sweep.Modernize.Diagnostics("OFR4303"), d => d["message"]!.GetValue<string>().Contains("\"/.nuget/nuget.targets\"", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonNode Package(JsonArray packages, string id) =>
        packages.Single(p => string.Equals(p!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))!;

    private static string Project(JsonNode diagnostic) => diagnostic["project"]?.GetValue<string>() ?? "";

    private static IEnumerable<string> Strings(JsonNode? array) => array?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
}
