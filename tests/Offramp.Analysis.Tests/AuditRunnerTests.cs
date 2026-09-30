using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;

namespace Offramp.Analysis.Tests;

public sealed class AuditRunnerTests
{
    private const string Legacy = "src/Behavior.Legacy/Behavior.Legacy.csproj";
    private const string Clean = "src/Behavior.Clean/Behavior.Clean.csproj";

    [Fact]
    [ProducesDiagnostic("OFR3001")]
    [ProducesDiagnostic("OFR3002")]
    [ProducesDiagnostic("OFR3003")]
    [ProducesDiagnostic("OFR3004")]
    [ProducesDiagnostic("OFR3005")]
    [ProducesDiagnostic("OFR3006")]
    [ProducesDiagnostic("OFR3007")]
    [ProducesDiagnostic("OFR3008")]
    [ProducesDiagnostic("OFR3009")]
    [ProducesDiagnostic("OFR3011")]
    [ProducesDiagnostic("OFR3013")]
    [ProducesDiagnostic("OFR3014")]
    public async Task Every_api_rule_has_a_positive_and_a_negative() =>
        await AssertPositivesAndNegatives(AuditKind.Api, extra: bag => Assert.Contains(bag.ToSortedList(),
            d => d.Code == "OFR3011" && d.Project == Legacy && d.Message.Contains("Microsoft.Web.Infrastructure", StringComparison.Ordinal)));

    [Fact]
    [ProducesDiagnostic("OFR3101")]
    [ProducesDiagnostic("OFR3102")]
    [ProducesDiagnostic("OFR3103")]
    [ProducesDiagnostic("OFR3104")]
    [ProducesDiagnostic("OFR3105")]
    [ProducesDiagnostic("OFR3106")]
    [ProducesDiagnostic("OFR3107")]
    [ProducesDiagnostic("OFR3108")]
    [ProducesDiagnostic("OFR3109")]
    [ProducesDiagnostic("OFR3110")]
    [ProducesDiagnostic("OFR3111")]
    [ProducesDiagnostic("OFR3112")]
    [ProducesDiagnostic("OFR3113")]
    [ProducesDiagnostic("OFR3114")]
    [ProducesDiagnostic("OFR3115")]
    [ProducesDiagnostic("OFR3116")]
    [ProducesDiagnostic("OFR3117")]
    [ProducesDiagnostic("OFR3118")]
    [ProducesDiagnostic("OFR3119")]
    [ProducesDiagnostic("OFR3120")]
    public async Task Every_behavior_rule_has_a_positive_and_a_negative() =>
        await AssertPositivesAndNegatives(AuditKind.Behavior, configRule: "OFR3116");

    [Fact]
    [ProducesDiagnostic("OFR3201")]
    [ProducesDiagnostic("OFR3202")]
    [ProducesDiagnostic("OFR3203")]
    [ProducesDiagnostic("OFR3204")]
    [ProducesDiagnostic("OFR3205")]
    [ProducesDiagnostic("OFR3210")]
    [ProducesDiagnostic("OFR3211")]
    public async Task Every_serialization_rule_has_a_positive_and_a_negative() =>
        await AssertPositivesAndNegatives(AuditKind.Serialization);

    [Fact]
    [ProducesDiagnostic("OFR3301")]
    [ProducesDiagnostic("OFR3302")]
    [ProducesDiagnostic("OFR3303")]
    [ProducesDiagnostic("OFR3310")]
    [ProducesDiagnostic("OFR3320")]
    public async Task Every_native_rule_has_a_positive_and_a_negative() =>
        await AssertPositivesAndNegatives(AuditKind.Native);

    [Fact]
    public async Task Serialization_tells_the_clone_idiom_from_file_persistence()
    {
        var (result, _) = await RunAsync("behavior", AuditKind.Serialization);
        var clone = Assert.Single(result.Findings, f => f.Rule == "OFR3202");
        Assert.Equal("transient", clone.Details["flow"]);

        var persisted = result.Findings.Where(f => f.Rule == "OFR3203").ToDictionary(f => f.Line);
        Assert.Equal(["file", "file", "memory-escapes", "parameter", "parameter"], persisted.Values.Select(f => f.Details["evidence"]).Order(StringComparer.Ordinal));

        var types = result.Findings.Where(f => f.Rule == "OFR3204").ToDictionary(f => f.Symbol);
        Assert.Equal("true", types["Behavior.Rules.Customer"].Details["iSerializable"]);
        Assert.Equal("true", types["Behavior.Rules.Payload"].Details["onDeserialized"]);
        Assert.Equal("Compute", types["Behavior.Rules.Payload"].Details["delegates"]);
        Assert.False(types["Behavior.Rules.Invoice"].Details.ContainsKey("delegates"), "a [NonSerialized] delegate is not serialized");

        // Line is serialized through Invoice.Body's field; PdfAttachment as an implementation of
        // IAttachment, the type of Invoice.Attachment, and PdfPage through PdfAttachment's field;
        // OFR3205.Positive by nobody.
        Assert.Equal(["Behavior.Rules.OFR3205.Positive"], result.Findings.Where(f => f.Rule == "OFR3205").Select(f => f.Symbol));
    }

    [Fact]
    public async Task A_removed_technology_is_not_also_missing_and_CallContext_is_not_remoting()
    {
        var (result, _) = await RunAsync("behavior", AuditKind.Api);
        var fixture = await ScannedFixtures.GetAsync("behavior");
        const string Api = "src/Behavior.Legacy/Rules/Api.cs";
        string[] RulesAt(string text)
        {
            var line = Array.FindIndex(File.ReadAllLines(Path.Combine(fixture.Root, Api)), l => l.Contains(text, StringComparison.Ordinal)) + 1;
            Assert.True(line > 0, $"'{text}' is not in {Api}");
            return [.. result.Findings.Where(f => f.File == Api && f.Line == line).Select(f => f.Rule).Order(StringComparer.Ordinal)];
        }

        // CallContext is its own rule, with AsyncLocal<T> as the answer: not Remoting, and not also "missing".
        Assert.Equal(["OFR3013"], RulesAt("CallContext.SetData"));
        Assert.Contains("AsyncLocal<T>", result.Findings.First(f => f.Rule == "OFR3013").Recommendation, StringComparison.Ordinal);
        Assert.Equal(["OFR3007"], RulesAt("RemotingConfiguration.Configure"));
        Assert.Equal(["OFR3008"], RulesAt("System.Activities.Activity activity"));
        Assert.Equal(["OFR3005"], RulesAt("public class Positive : System.Web.Services.WebService"));

        // Security transparency attributes do nothing on the target (info); CAS permission attributes are gone (OFR3009).
        Assert.Equal(["OFR3014"], RulesAt("[System.Security.SecurityCritical]").Distinct());
        Assert.All(result.Findings.Where(f => f.Rule == "OFR3014"), f => Assert.Equal(Severity.Info, f.Severity));
        Assert.DoesNotContain(result.Findings, f => f.Rule == "OFR3009" && f.Symbol.StartsWith("System.Security.Security", StringComparison.Ordinal));
        Assert.Contains(result.Findings, f => f.Rule == "OFR3009" && f.Symbol == "System.Security.Permissions.SecurityPermissionAttribute");

        // A missing API with a known replacement names it.
        var dynamic = Assert.Single(result.Findings, f => f.Rule == "OFR3001" && f.Symbol.StartsWith("System.AppDomain.DefineDynamicAssembly", StringComparison.Ordinal));
        Assert.Equal("System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly", dynamic.Details["replacement"]);
        Assert.EndsWith("does not exist on the target. Use System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly.", dynamic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3011")]
    public async Task Packages_config_packages_are_compiled_for_the_target_instead_of_their_framework_dlls()
    {
        // Shop.Web gets MVC 5 and Web API 2 from packages.config; its HintPaths are the net45 DLLs.
        var (result, diagnostics) = await RunAsync("mvc5", AuditKind.Api);

        var missing = result.Findings.Where(f => f.Rule == "OFR3001").ToList();
        Assert.All(missing.Where(f => f.Symbol == "System.Web.Mvc.Controller"), f => Assert.Equal("System.Web.Mvc", f.Details["assembly"]));
        Assert.Contains(missing, f => f.Symbol == "System.Web.Mvc.Controller");
        Assert.Contains(missing, f => f.Symbol == "System.Web.Http.ApiController");
        Assert.Contains(missing, f => f.Symbol == "System.Web.Mvc.ActionResult");

        // The packages without net10.0 assets are named; Web API's client and Json.NET support it.
        var dropped = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR3011");
        Assert.Equal(
            ["Microsoft.AspNet.Mvc", "Microsoft.AspNet.Razor", "Microsoft.AspNet.WebApi", "Microsoft.AspNet.WebApi.Core", "Microsoft.AspNet.WebApi.WebHost", "Microsoft.AspNet.WebPages", "Microsoft.Web.Infrastructure"],
            dropped.Data["packages"]!.AsArray().Select(p => p!.GetValue<string>()));
    }

    [Fact]
    [ProducesDiagnostic("OFR3015")]
    public async Task A_package_nuget_cannot_find_keeps_its_dll_and_is_reported()
    {
        var fixture = await ScannedFixtures.GetAsync("mvc5");
        var processes = new FakeProcessRunner().On(spec => spec.FileName == "dotnet", spec =>
            File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "references.csproj")).Contains("\"Microsoft.AspNet.Mvc\"", StringComparison.Ordinal)
                ? new ProcessResult(1, "references.csproj : error NU1102: Unable to find package Microsoft.AspNet.Mvc with version (= 5.2.9)\n", "")
                : ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult());
        var bag = new DiagnosticBag();

        var result = await AuditRunner.RunAsync(Request(fixture, AuditKind.Api, bag) with
        {
            References = new TargetReferenceResolver(fixture.Root, processes, NullCache.Instance),
        });

        var unavailable = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR3015");
        Assert.Equal(("src/Shop.Web/Shop.Web.csproj", "Microsoft.AspNet.Mvc"), (unavailable.Project, Assert.Single(unavailable.Data["packages"]!.AsArray())!.GetValue<string>()));

        // Its DLL is referenced as the project records it; the other packages are resolved for the target.
        var missing = result.Findings.Where(f => f.Rule == "OFR3001").Select(f => f.Symbol).ToList();
        Assert.DoesNotContain("System.Web.Mvc.Controller", missing);
        Assert.Contains("System.Web.Http.ApiController", missing);
    }

    [Fact]
    public async Task A_winforms_library_is_compiled_for_windows_and_its_removed_controls_throw()
    {
        // Editor.Controls is a library (not kind winforms) that references System.Windows.Forms.
        var (result, diagnostics) = await RunAsync("winforms-library", AuditKind.Api);

        Assert.DoesNotContain(diagnostics.ToSortedList(), d => d.Code == "OFR3010");
        Assert.DoesNotContain(result.Findings, f => f.Rule is "OFR3001" or "OFR3002");
        var shims = result.Findings.Where(f => f.Rule == "OFR3003").ToList();
        Assert.Contains(shims, f => f.Symbol == "System.Windows.Forms.MenuItem");
        Assert.Contains(shims, f => f.Symbol == "System.Windows.Forms.ContextMenu");
        Assert.All(shims, f => Assert.Equal("WFDEV006", f.Details["diagnosticId"]));
    }

    [Fact]
    [ProducesDiagnostic("OFR3016")]
    public async Task A_project_whose_build_failed_is_named_as_such()
    {
        var fixture = await ScannedFixtures.GetAsync("behavior");
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        var bag = new DiagnosticBag();
        var partial = model with
        {
            Projects = [.. model.Projects.Select(p => p.Id switch
            {
                Legacy => p with { Partial = true },
                Clean => p with { Partial = true, CompilerCalls = new SortedDictionary<string, CompilerCallRef>(StringComparer.Ordinal) },
                _ => p,
            })],
        };

        var result = await AuditRunner.RunAsync(Request(fixture, AuditKind.Behavior, bag) with { Model = partial });

        // The project whose compiler call failed is audited, and the audit says the build failed.
        Assert.Contains(result.Findings, f => f.Project == Legacy);
        Assert.Contains("its build failed during `offramp scan`", Assert.Single(bag.ToSortedList(), d => d.Code == "OFR3016").Message, StringComparison.Ordinal);

        // The one without a compiler call is not audited, and the reason is the build, not a missing scan.
        var skipped = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR3012");
        Assert.Equal(Clean, skipped.Project);
        Assert.Contains("its build failed (OFR0130)", skipped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(run `offramp scan`)", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fully_qualified_name_is_reported_at_its_type_not_its_namespace()
    {
        var (result, _) = await RunAsync("behavior", AuditKind.Api);
        var api = result.Findings.Where(f => f.File == "src/Behavior.Legacy/Rules/Api.cs").ToList();

        var context = Assert.Single(api, f => f.Rule == "OFR3001" && f.Symbol == "System.Web.HttpContext");
        Assert.Equal("System.Web", context.Namespace);
        Assert.DoesNotContain(api, f => f.Symbol is "System.Web" or "System.Web.UI" or "System.Activities");

        // A removed technology named in full is reported at its type too, as that technology (not also as missing).
        Assert.Equal("System.Web.UI", Assert.Single(api, f => f.Symbol == "System.Web.UI.Control").Namespace);
        Assert.Equal(["OFR3004"], api.Where(f => f.Symbol == "System.Web.UI.Control").Select(f => f.Rule));
        Assert.Equal(["OFR3008"], api.Where(f => f.Symbol == "System.Activities.Activity").Select(f => f.Rule));
    }

    [Fact]
    [ProducesDiagnostic("OFR3012")]
    public async Task A_base_type_missing_on_the_target_is_not_blamed_on_the_names_inside_a_derived_class()
    {
        // EditSettings, in a web project, derives from System.Web.UI.UserControl through another
        // project's ModuleBase. On the target, Roslyn reports "UserControl could not be found" at
        // every name looked up inside it, including Convert, EventArgs, and ModuleBase itself.
        var (result, diagnostics) = await RunAsync("webforms", AuditKind.Api);

        // What the class really uses from Web Forms is reported as Web Forms (OFR3004, not also as missing).
        Assert.Equal(
            ["OFR3004 Portal.Controls.ModuleBase", "OFR3004 System.Web.UI.Control.ClientID", "OFR3004 System.Web.UI.Control.ViewState"],
            result.Findings.Where(f => f.File == "src/Portal.Modules/EditSettings.ascx.cs").Select(f => $"{f.Rule} {f.Symbol}").Order(StringComparer.Ordinal));
        Assert.Equal(["OFR3004"], result.Findings.Where(f => f.Symbol == "System.Web.UI.UserControl" && f.File == "src/Portal.Controls/ModuleBase.cs").Select(f => f.Rule));
        var missing = result.Findings.Where(f => f.Rule == "OFR3001").ToList();
        Assert.All(missing, f => Assert.Equal("System.Web", f.Details["assembly"]));

        // PortalException derives from the missing HttpException; ErrorCode, inherited from
        // ExternalException, exists on the target although the name cannot be looked up there.
        Assert.Contains(missing, f => f.Symbol == "System.Web.HttpException" && f.File == "src/Portal.Modules/PortalException.cs");
        Assert.DoesNotContain(missing, f => f.Symbol.Contains("ErrorCode", StringComparison.Ordinal));

        // Portal.Controls' extension of HttpRequestBase is missing because HttpRequestBase is: it belongs to System.Web.
        var extension = Assert.Single(missing, f => f.Symbol == "System.Web.HttpRequestBase.IsSecure()");
        Assert.Equal(("Portal.Controls", "System.Web"), (extension.Details["extensionAssembly"], extension.Namespace));

        // The Visual Basic library is not audited, and says so.
        Assert.Equal(["src/Portal.Utilities/Portal.Utilities.vbproj: audits read C# only."], result.Skipped);
        var skipped = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR3012");
        Assert.Equal(("src/Portal.Utilities/Portal.Utilities.vbproj", "Not audited: audits read C# only."), (skipped.Project, skipped.Message));
    }

    [Fact]
    public async Task Audit_api_on_netfx_only_maps_system_web_and_system_drawing()
    {
        var (result, _) = await RunAsync("netfx-only", AuditKind.Api);

        var missing = result.Findings.Where(f => f.Rule == "OFR3001").ToList();
        var context = Assert.Single(missing, f => f.Symbol == "System.Web.HttpContext");
        Assert.Equal("none", context.Details["mapping"]);
        Assert.Equal("System.Web", context.Details["assembly"]);
        Assert.Contains("ASP.NET (System.Web) has no port", context.Message, StringComparison.Ordinal);

        var bitmap = missing.Where(f => f.Symbol == "System.Drawing.Bitmap").ToList();
        Assert.Equal([13, 15], bitmap.Select(f => f.Line));
        Assert.All(bitmap, f => Assert.Equal(("package", "System.Drawing.Common", "src/Legacy.Core/Thumbnails.cs"), (f.Details["mapping"], f.Details["package"], f.File)));

        // HttpUtility and Size exist on the target, so they are not missing.
        Assert.DoesNotContain(missing, f => f.Symbol.Contains("HttpUtility", StringComparison.Ordinal) || f.Symbol.Contains("Size", StringComparison.Ordinal));

        var core = Assert.Single(result.Ledger, l => l.Project == "src/Legacy.Core/Legacy.Core.csproj");
        Assert.Equal(2, core.Files);
        Assert.Equal(0, core.PortableFiles);
        Assert.Equal(1, Assert.Single(result.Ledger, l => l.Project == "src/Legacy.App/Legacy.App.csproj").Portability);
        Assert.Equal(["System.Drawing", "System.Web"], result.TopNamespaces.Select(n => n.Namespace).Order(StringComparer.Ordinal));
    }

    [Fact]
    [ProducesDiagnostic("OFR3010")]
    public async Task A_target_that_cannot_be_resolved_is_reported_and_symbol_rules_still_run()
    {
        var fixture = await ScannedFixtures.GetAsync("netfx-only");
        var processes = new FakeProcessRunner().On("dotnet", ["msbuild"], 1, "", "error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.");
        var bag = new DiagnosticBag();

        var result = await AuditRunner.RunAsync(Request(fixture, AuditKind.Api, bag) with
        {
            References = new TargetReferenceResolver(fixture.Root, processes, NullCache.Instance),
        });

        Assert.DoesNotContain(result.Findings, f => f.Rule is "OFR3001" or "OFR3002");
        var reported = bag.ToSortedList().Where(d => d.Code == "OFR3010").ToList();
        Assert.Equal(2, reported.Count);
        Assert.Contains("NU1301", reported[0].Message, StringComparison.Ordinal);
        Assert.Contains(result.Skipped, s => s.StartsWith("src/Legacy.Core/Legacy.Core.csproj: not compiled against net10.0", StringComparison.Ordinal));
    }

    /// <summary>
    /// NHibernate P1 #7: about 50 of the 71 errors of NHibernate's netstandard2.0 build are Reflection.Emit, which
    /// net10.0 has, so an audit against net10.0 reported none of them. Against netstandard2.0 the library compiles
    /// against .NET Standard's reference assemblies, and the .NET Standard symbols replace .NET Framework's.
    /// </summary>
    [Fact]
    public async Task A_standard_target_reports_what_net_has_and_the_standard_lacks()
    {
        var fixture = await ScannedFixtures.GetAsync("behavior");
        var bag = new DiagnosticBag();

        var standard = await AuditRunner.RunAsync(Request(fixture, AuditKind.Api, bag) with { Target = ModernTarget.Parse("netstandard2.0"), Projects = [Legacy] });
        var (net, _) = await RunAsync("behavior", AuditKind.Api);

        static bool Emit(AuditFinding f) => f.Rule == "OFR3001" && f.Symbol == "System.Reflection.Emit.ILGenerator";
        Assert.Equal("netstandard2.0", standard.Target);
        var finding = Assert.Single(standard.Findings, Emit);
        Assert.Equal("src/Behavior.Legacy/Rules/Api.cs", finding.File);
        Assert.DoesNotContain(net.Findings, Emit);
        Assert.Contains(standard.Findings, f => f.Rule == "OFR3001" && f.Symbol == "System.Web.HttpContext");
        Assert.DoesNotContain(bag.ToSortedList(), d => d.Code is "OFR3010" or "OFR3017");
    }

    /// <summary>Under a .NET Standard target a project that runs is compiled against .NET 10, and says so (ADR 0057).</summary>
    [Fact]
    [ProducesDiagnostic("OFR3017")]
    public async Task Under_a_standard_target_an_application_is_compiled_against_net()
    {
        var fixture = await ScannedFixtures.GetAsync("netfx-only");
        var bag = new DiagnosticBag();

        var result = await AuditRunner.RunAsync(Request(fixture, AuditKind.Api, bag) with { Target = ModernTarget.Parse("netstandard2.0") });

        var diagnostic = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR3017");
        Assert.Equal("src/Legacy.App/Legacy.App.csproj", diagnostic.Project);
        Assert.Equal("src/Legacy.App/Legacy.App.csproj is a console project, which needs a .NET to run on and netstandard2.0 is not one, so it was compiled against net10.0.", diagnostic.Message);
        Assert.Contains(result.Findings, f => f.Project == "src/Legacy.Core/Legacy.Core.csproj" && f.Rule == "OFR3001" && f.Symbol.StartsWith("System.Web.", StringComparison.Ordinal));
        Assert.DoesNotContain(bag.ToSortedList(), d => d.Code == "OFR3010");
    }

    [Fact]
    public void Standard_symbols_replace_the_framework_ones()
    {
        var recorded = new CSharpParseOptions(preprocessorSymbols: ["NETFRAMEWORK", "NET48", "NET472_OR_GREATER", "TRACE", "DEBUG"]);

        Assert.Equal(
            ["DEBUG", "NETSTANDARD", "NETSTANDARD1_0_OR_GREATER", "NETSTANDARD1_1_OR_GREATER", "NETSTANDARD1_2_OR_GREATER", "NETSTANDARD1_3_OR_GREATER",
                "NETSTANDARD1_4_OR_GREATER", "NETSTANDARD1_5_OR_GREATER", "NETSTANDARD1_6_OR_GREATER", "NETSTANDARD2_0", "NETSTANDARD2_0_OR_GREATER", "TRACE"],
            TargetCompilation.Options(recorded, TargetCompilation.SymbolsFor("netstandard2.0")).PreprocessorSymbolNames);
        Assert.Contains("NET8_0", TargetCompilation.SymbolsFor("net8.0-windows"));
        Assert.Contains("WINDOWS", TargetCompilation.SymbolsFor("net8.0-windows"));
        Assert.Contains("NETSTANDARD2_1_OR_GREATER", TargetCompilation.SymbolsFor("netstandard2.1"));
    }

    [Fact]
    public async Task Overrides_change_severity_and_none_disables_a_rule()
    {
        var fixture = await ScannedFixtures.GetAsync("behavior");
        var bag = new DiagnosticBag();
        var overrides = new Dictionary<string, SeverityOverride>
        {
            ["OFR3101"] = new(null, "ICU is fine for us"),
            ["OFR3109"] = new(Severity.Error, "we store these"),
        };

        var result = await AuditRunner.RunAsync(Request(fixture, AuditKind.Behavior, bag) with { Overrides = overrides });

        Assert.DoesNotContain("OFR3101", result.Rules);
        Assert.DoesNotContain(result.Findings, f => f.Rule == "OFR3101");
        Assert.All(result.Findings.Where(f => f.Rule == "OFR3109"), f => Assert.True(f.Severity == Severity.Error && f.Overridden));
        Assert.All(result.Findings.Where(f => f.Rule == "OFR3113"), f => Assert.False(f.Overridden));
    }

    [Fact]
    public async Task Packs_select_rules_and_disabled_packs_drop_them()
    {
        var fixture = await ScannedFixtures.GetAsync("behavior");

        var web = await AuditRunner.RunAsync(Request(fixture, AuditKind.Behavior, new DiagnosticBag()) with { Packs = ["web"] });
        var noWeb = await AuditRunner.RunAsync(Request(fixture, AuditKind.Behavior, new DiagnosticBag()) with { DisabledPacks = ["web", "data"] });

        Assert.Equal(["OFR3106", "OFR3117", "OFR3118"], web.Rules);
        Assert.All(web.Findings, f => Assert.Contains(f.Rule, web.Rules));
        Assert.DoesNotContain(noWeb.Findings, f => f.Rule is "OFR3106" or "OFR3108" or "OFR3117" or "OFR3118");
        Assert.Contains(noWeb.Findings, f => f.Rule == "OFR3101");
    }

    [Theory]
    [InlineData(8, Severity.Warning)]
    [InlineData(9, Severity.Error)]
    public void The_insecure_serializer_is_an_error_from_net_9(int target, Severity expected) =>
        Assert.Equal(expected, AuditRules.For(AuditKind.Serialization).Single(r => r.Id == "OFR3201").SeverityFor(target));

    [Fact]
    public void Every_rule_is_in_the_catalog_with_its_title_and_severity()
    {
        foreach (var rule in AuditRules.Every)
        {
            var descriptor = DiagnosticCatalog.Find(rule.Id);
            Assert.True(descriptor is not null, $"{rule.Id} is not in DiagnosticCatalog.");
            Assert.Equal(rule.Title, descriptor.Title);
            Assert.Equal(rule.Severity, descriptor.DefaultSeverity);
            Assert.Equal(rule.Recommendation, descriptor.Fix);
        }
    }

    [Fact]
    public async Task Findings_are_deterministic()
    {
        var first = await RunAsync("behavior", AuditKind.Behavior);
        var second = await RunAsync("behavior", AuditKind.Behavior);

        Assert.Equal(
            first.Result.Findings.Select(f => $"{f.Rule} {f.File}:{f.Line}:{f.Column} {f.Symbol}"),
            second.Result.Findings.Select(f => $"{f.Rule} {f.File}:{f.Line}:{f.Column} {f.Symbol}"));
    }

    private static async Task AssertPositivesAndNegatives(AuditKind audit, string? configRule = null, Action<DiagnosticBag>? extra = null)
    {
        var (result, bag) = await RunAsync("behavior", audit);
        var fixture = await ScannedFixtures.GetAsync("behavior");
        var ranges = Ranges(fixture.Root);
        var failures = new List<string>();

        foreach (var rule in AuditRules.For(audit))
        {
            var mine = result.Findings.Where(f => f.Rule == rule.Id).ToList();
            if (rule.Id == configRule)
            {
                if (!mine.Any(f => f.File == "src/Behavior.Legacy/app.config"))
                {
                    failures.Add($"{rule.Id}: no finding in Behavior.Legacy's app.config");
                }

                failures.AddRange(mine.Where(f => f.Project == Clean).Select(f => $"{rule.Id}: finding in Behavior.Clean at {f.File}:{f.Line}"));
                continue;
            }

            if (!ranges.TryGetValue(rule.Id, out var members))
            {
                failures.Add($"{rule.Id}: no class {rule.Id} in the behavior fixture");
                continue;
            }

            if (!mine.Any(f => members.Any(m => m.Positive && m.Contains(f))))
            {
                failures.Add($"{rule.Id}: no finding in a Positive member");
            }

            failures.AddRange(mine.Where(f => members.Any(m => !m.Positive && m.Contains(f))).Select(f => $"{rule.Id}: finding in a Negative member at {f.File}:{f.Line}"));
        }

        failures.AddRange(result.Findings.Where(f => f.Project == Clean).Select(f => $"{f.Rule}: finding in Behavior.Clean at {f.File}:{f.Line}"));
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // Every rule with findings is one diagnostic per project.
        foreach (var group in result.Findings.GroupBy(f => (f.Rule, f.Project)))
        {
            Assert.Single(bag.ToSortedList(), d => d.Code == group.Key.Rule && d.Project == group.Key.Project);
        }

        extra?.Invoke(bag);
    }

    private static async Task<(AuditResult Result, DiagnosticBag Diagnostics)> RunAsync(string fixtureName, AuditKind audit)
    {
        var fixture = await ScannedFixtures.GetAsync(fixtureName);
        var bag = new DiagnosticBag();
        return (await AuditRunner.RunAsync(Request(fixture, audit, bag)), bag);
    }

    private static AuditRequest Request(ScannedFixture fixture, AuditKind audit, DiagnosticBag bag) => new()
    {
        RepositoryRoot = fixture.Root,
        Model = WorkspaceStore.Read(fixture.WorkspacePath),
        Audit = audit,
        Target = ModernTarget.Default,
        Diagnostics = bag,
        References = new TargetReferenceResolver(fixture.Root, ProcessRunner.Instance, new FileCache(Path.Combine(fixture.Root, ".offramp", "cache"))),
    };

    private sealed record Member(string File, int Start, int End, bool Positive)
    {
        public bool Contains(AuditFinding finding) => finding.File == File && finding.Line >= Start && finding.Line <= End;
    }

    /// <summary>Rule class name → its Positive* and Negative* members (methods and nested types) as line ranges.</summary>
    private static Dictionary<string, List<Member>> Ranges(string root)
    {
        var ranges = new Dictionary<string, List<Member>>(StringComparer.Ordinal);
        var directory = Path.Combine(root, "src", "Behavior.Legacy", "Rules");
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var file = "src/Behavior.Legacy/Rules/" + Path.GetFileName(path);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText.StartsWith("OFR", StringComparison.Ordinal)))
            {
                var members = new List<Member>();
                foreach (var member in type.Members)
                {
                    var name = member switch
                    {
                        MethodDeclarationSyntax method => method.Identifier.ValueText,
                        BaseTypeDeclarationSyntax nested => nested.Identifier.ValueText,
                        _ => "",
                    };
                    var span = member.GetLocation().GetLineSpan();
                    if (name.StartsWith("Positive", StringComparison.Ordinal) || name.StartsWith("Negative", StringComparison.Ordinal))
                    {
                        members.Add(new Member(file, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, name.StartsWith("Positive", StringComparison.Ordinal)));
                    }
                }

                // A rule's class can be partial: its members are those of every part.
                (ranges.TryGetValue(type.Identifier.ValueText, out var known) ? known : ranges[type.Identifier.ValueText] = []).AddRange(members);
            }
        }

        return ranges;
    }
}
