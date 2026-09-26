using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Core.Caching;
using Offramp.Core.Diagnostics;
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

        // Line is serialized through Invoice.Body's field; OFR3205.Positive by nobody.
        Assert.Equal(["Behavior.Rules.OFR3205.Positive"], result.Findings.Where(f => f.Rule == "OFR3205").Select(f => f.Symbol));
    }

    [Fact]
    public async Task A_fully_qualified_name_is_reported_at_its_type_not_its_namespace()
    {
        var (result, _) = await RunAsync("behavior", AuditKind.Api);

        var control = Assert.Single(result.Findings, f => f.Rule == "OFR3001" && f.Symbol == "System.Web.UI.Control");
        Assert.Equal("System.Web.UI", control.Namespace);
        Assert.DoesNotContain(result.Findings, f => f.Rule == "OFR3001" && f.Symbol is "System.Web.UI" or "System.Activities");
        Assert.Contains(result.Findings, f => f.Rule == "OFR3001" && f.Symbol == "System.Activities.Activity");
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
        TargetMajor = 10,
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

                ranges[type.Identifier.ValueText] = members;
            }
        }

        return ranges;
    }
}
