using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NuGet.Versioning;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Newtonsoft;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Fixtures;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Tests.Newtonsoft;

/// <summary>
/// <c>newtonsoft scan</c> over the two fixtures, and one test per diagnostic. Behaviors that 7.0.1
/// and 13.x share are tested by giving a project another release in the model; code a fixture
/// lacks is added to the project's compilation.
/// </summary>
public sealed class NewtonsoftScannerTests
{
    private const string Catalog = "src/Catalog/Catalog.csproj";
    private const string Inventory = "src/Inventory/Inventory.csproj";
    private static readonly NuGetVersion Target = NuGetVersion.Parse("13.0.4");

    [Theory]
    [InlineData("newtonsoft-sdk")]
    [InlineData("newtonsoft-legacy")]
    [ProducesDiagnostic("OFR3702")]
    [ProducesDiagnostic("OFR3703")]
    [ProducesDiagnostic("OFR3704")]
    [ProducesDiagnostic("OFR3705")]
    [ProducesDiagnostic("OFR3707")]
    [ProducesDiagnostic("OFR3708")]
    [ProducesDiagnostic("OFR3709")]
    [ProducesDiagnostic("OFR3719")]
    [ProducesDiagnostic("OFR3721")]
    public async Task Scan_matches_the_snapshot(string fixture)
    {
        var (result, diagnostics) = Scan(await ScannedFixtures.GetAsync(fixture));

        await Verify(Render(result, diagnostics), extension: "txt").UseParameters(fixture);
    }

    [Fact]
    public async Task The_sdk_fixture_seeds_every_rule_that_differs_between_7_and_13()
    {
        var (result, _) = Scan(await ScannedFixtures.GetAsync("newtonsoft-sdk"));

        var rules = result.Findings.Select(f => f.Rule).ToHashSet(StringComparer.Ordinal);
        Assert.Superset(new HashSet<string>(StringComparer.Ordinal) { "OFR3703", "OFR3704", "OFR3705", "OFR3707", "OFR3708", "OFR3709", "OFR3719" }, rules);
        var camel = Assert.Single(result.Findings, f => f.Rule == "OFR3703");
        Assert.Equal("Contoso.Catalog.Product.LegacyCode", camel.Symbol);
        Assert.Equal("legacY CODE", camel.Details["old"]);
        Assert.Equal("legacy CODE", camel.Details["new"]);
        Assert.Contains(result.Findings, f => f.Rule == "OFR3719" && f.Symbol == "Contoso.Catalog.Product.Attributes");
    }

    [Fact]
    [ProducesDiagnostic("OFR3701")]
    public async Task A_removed_api_is_found_by_compiling_against_the_target()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, diagnostics) = Scan(scanned, r => r with
        {
            Compilations = new AddedSource(r.RepositoryRoot, Catalog, """
                namespace Contoso.Catalog
                {
                    public static class Async
                    {
                        public static System.Threading.Tasks.Task<string> Write(Product product) => Newtonsoft.Json.JsonConvert.SerializeObjectAsync(product);
                    }
                }
                """),
        });

        var removed = Assert.Single(result.Findings, f => f.Rule == "OFR3701");
        Assert.StartsWith("M:Newtonsoft.Json.JsonConvert.SerializeObjectAsync", removed.Symbol, StringComparison.Ordinal);
        Assert.Contains("Task.Run", removed.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3701" && d.Project == Catalog);
    }

    [Fact]
    public async Task A_shared_library_is_judged_against_the_old_deployed_application_release()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var model = WithVersion(scanned.Outcome.Model!, Catalog, "13.0.4");
        model = model with { Projects = [.. model.Projects.Select(p => p.Id == Catalog ? p with { Kind = ProjectKind.Library, OutputType = "Library" } : p)] };
        var application = new ProjectInfo
        {
            Id = "src/Runner/Runner.csproj",
            Name = "Runner",
            Kind = ProjectKind.Console,
            OutputType = "Exe",
            TargetFrameworks = ["net48"],
            ProjectReferences = [Catalog],
            PackageReferences = [new PackageReferenceInfo { Id = "Newtonsoft.Json", Version = "7.0.1" }],
            AssemblyReferences = [new AssemblyReferenceInfo { Name = "Newtonsoft.Json", Kind = AssemblyReferenceKind.File, HintPath = "packages/Newtonsoft.Json.7.0.1/lib/net45/Newtonsoft.Json.dll" }],
            Resolved = new SortedDictionary<string, ResolvedFramework>(StringComparer.Ordinal)
            {
                ["net48"] = new() { Packages = [new ResolvedPackage { Id = "Newtonsoft.Json", Version = "7.0.1", Direct = true }] },
            },
        };
        model = model with { Projects = [.. model.Projects, application] };
        var (result, diagnostics) = Scan(scanned, request => request with { Model = model });

        var report = result.Projects.Single(p => p.Project == Catalog);
        Assert.Equal(["7.0.1"], report.DeployedReleases);
        Assert.Equal(["src/Runner/Runner.csproj"], report.DeployedBy);
        Assert.True(report.InScope);
        Assert.Contains(result.Findings, f => f.Project == Catalog && f.Rule == "OFR3703" && f.Current == "old");
        Assert.DoesNotContain(diagnostics.ToSortedList(), d => d.Code == "OFR3726");
    }

    [Fact]
    [ProducesDiagnostic("OFR3726")]
    public async Task Projects_with_only_target_deployments_are_omitted_by_default()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var model = scanned.Outcome.Model!;
        foreach (var project in model.Projects)
        {
            model = WithVersion(model, project.Id, "13.0.4");
        }

        model = model with { Projects = [.. model.Projects.Select(p => p with { Kind = ProjectKind.Library, OutputType = "Library" })] };
        var (result, diagnostics) = Scan(scanned, request => request with { Model = model });

        Assert.All(result.Projects, project =>
        {
            Assert.False(project.InScope);
            Assert.Empty(project.Sites);
        });
        Assert.Contains(diagnostics.ToSortedList(), diagnostic => diagnostic.Code == "OFR3726");
    }

    [Fact]
    public async Task Without_the_target_release_nothing_differs_and_nothing_is_reported()
    {
        // The detectors and the trial compilation can find nothing when the versions agree: the
        // project at the target release gets OFR3724 and no findings.
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, diagnostics) = Scan(scanned, r => r with { Model = WithVersion(r.Model, Catalog, "13.0.4"), AllProjects = true });

        Assert.DoesNotContain(result.Findings, f => f.Project == Catalog);
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3724" && d.Project == Catalog);
    }

    [Fact]
    [ProducesDiagnostic("OFR3706")]
    [ProducesDiagnostic("OFR3710")]
    [ProducesDiagnostic("OFR3713")]
    [ProducesDiagnostic("OFR3722")]
    [ProducesDiagnostic("OFR3724")]
    public async Task A_project_on_10_loses_decimal_scale_that_13_keeps()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, diagnostics) = Scan(scanned, r => r with
        {
            Model = WithVersion(WithVersion(r.Model, Catalog, "10.0.3"), Inventory, "13.0.4"),
            AllProjects = true,
            Compilations = new AddedSource(r.RepositoryRoot, Catalog, """
                using System;
                using System.Collections.Immutable;
                using Newtonsoft.Json;
                using Newtonsoft.Json.Linq;
                namespace System.Collections.Immutable
                {
                    // net48 has no System.Collections.Immutable; the detector goes by the interface's name.
                    public interface IImmutableSet<T> : System.Collections.Generic.IEnumerable<T> { }
                }
                namespace Contoso.Catalog
                {
                    public class Shipment { public DateTime Sent { get; set; } public IImmutableSet<string> Labels { get; set; } }
                    public static class Shipments
                    {
                        public static Shipment Read(JToken token) => token.ToObject<Shipment>(JsonSerializer.Create(new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Local }));
                    }
                }
                """),
        });

        Assert.Contains(result.Findings, f => f.Rule == "OFR3706" && f.Case == "decimal-scale" && f.Symbol == "Contoso.Catalog.Product.Price" && f.Current == "dropped" && f.Target == "kept");
        Assert.Contains(result.Findings, f => f.Rule == "OFR3710" && f.Symbol == "Contoso.Catalog.Shipment.Sent");
        Assert.Contains(result.Findings, f => f.Rule == "OFR3713" && f.Symbol == "Contoso.Catalog.Shipment.Labels");
        Assert.Contains(result.Findings, f => f.Rule == "OFR3704" && f.Current == "get-object-data");
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3722" && d.Message.Contains("10.0.3", StringComparison.Ordinal));
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3724" && d.Project == Inventory);
    }

    [Fact]
    [ProducesDiagnostic("OFR3711")]
    [ProducesDiagnostic("OFR3712")]
    public async Task A_project_on_11_0_1_wrote_data_contract_collections_as_objects_and_could_not_write_exceptions()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, _) = Scan(scanned, r => r with
        {
            Model = WithVersion(r.Model, Catalog, "11.0.1"),
            Compilations = new AddedSource(r.RepositoryRoot, Catalog, """
                using System;
                using System.Collections;
                using System.Collections.Generic;
                using System.Runtime.Serialization;
                using Newtonsoft.Json;
                namespace Contoso.Catalog
                {
                    [DataContract] public class Shelf : IEnumerable<int> { public IEnumerator<int> GetEnumerator() { yield return 1; } IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
                    public class StockError : Exception { }
                    public static class Reports
                    {
                        public static string Shelf(Shelf shelf) => JsonConvert.SerializeObject(shelf);
                        public static string Error(StockError error) => JsonConvert.SerializeObject(error);
                    }
                }
                """),
        });

        Assert.Contains(result.Findings, f => f.Rule == "OFR3711" && f.Symbol == "Contoso.Catalog.Shelf" && f.Current == "object");
        Assert.Contains(result.Findings, f => f.Rule == "OFR3712" && f.Symbol == "Contoso.Catalog.StockError" && f.Current == "throws");
    }

    [Fact]
    [ProducesDiagnostic("OFR3720")]
    public async Task A_payload_that_cannot_be_typed_is_reported()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, diagnostics) = Scan(scanned, r => r with
        {
            Compilations = new AddedSource(r.RepositoryRoot, Catalog, """
                namespace Contoso.Catalog
                {
                    public static class Untyped
                    {
                        private static object last;
                        public static string Write() => Newtonsoft.Json.JsonConvert.SerializeObject(last);
                    }
                }
                """),
        });

        Assert.Contains(result.Projects.Single(p => p.Project == Catalog).Sites, s => s.Id == "Contoso.Catalog.Untyped.Write#1" && s.PayloadTypes.Count == 0);
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3720" && d.Message.Contains("Untyped.Write#1", StringComparison.Ordinal));
    }

    [Fact]
    [ProducesDiagnostic("OFR3723")]
    public async Task A_loose_dll_is_reported_and_not_judged()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var model = scanned.Outcome.Model!;
        var loose = model with
        {
            Projects = [.. model.Projects.Select(p => p.Id != Catalog ? p : p with
            {
                PackageReferences = [.. p.PackageReferences.Where(r => r.Id != "Newtonsoft.Json")],
                Resolved = new SortedDictionary<string, ResolvedFramework>(p.Resolved.ToDictionary(r => r.Key, r => r.Value with { Packages = [.. r.Value.Packages.Where(k => k.Id != "Newtonsoft.Json")] }), StringComparer.Ordinal),
                AssemblyReferences = [.. p.AssemblyReferences, new AssemblyReferenceInfo { Name = "Newtonsoft.Json", HintPath = "lib/Newtonsoft.Json.dll", Kind = AssemblyReferenceKind.File, Metadata = new AssemblyFileMetadata { AssemblyVersion = "7.0.0.0" } }],
            })],
        };
        var (result, diagnostics) = Scan(scanned, r => r with { Model = loose });

        Assert.Equal(NewtonsoftReferenceKind.LooseDll, result.Projects.Single(p => p.Project == Catalog).Newtonsoft.Via);
        Assert.DoesNotContain(result.Findings, f => f.Project == Catalog);
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3723" && d.Project == Catalog);
    }

    [Fact]
    [ProducesDiagnostic("OFR3725")]
    public async Task Without_the_target_package_the_trial_compilation_is_skipped()
    {
        var (result, diagnostics) = Scan(await ScannedFixtures.GetAsync("newtonsoft-legacy"), r => r with { TargetPackage = null });

        Assert.DoesNotContain(result.Findings, f => f.Rule is "OFR3701" or "OFR3702");
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3725");
    }

    [Fact]
    public async Task A_rule_turned_off_in_offramp_yml_reports_nothing()
    {
        var (result, _) = Scan(await ScannedFixtures.GetAsync("newtonsoft-sdk"), r => r with
        {
            Overrides = new Dictionary<string, SeverityOverride> { ["OFR3709"] = new(null, "trees are shallow"), ["OFR3704"] = new(Severity.Error, "stored data") },
        });

        Assert.DoesNotContain(result.Findings, f => f.Rule == "OFR3709");
        Assert.Contains(result.Findings, f => f.Rule == "OFR3704" && f.Severity == Severity.Error && f.Overridden);
    }

    [Fact]
    public async Task Each_site_has_an_entry_point_or_the_reason_it_has_none()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, _) = Scan(scanned, r => r with
        {
            EntryDepth = 1,
            Compilations = new AddedSource(r.RepositoryRoot, Catalog, """
                using System;
                using Newtonsoft.Json;
                namespace Contoso.Catalog
                {
                    public interface IClock { DateTime Read(); }
                    public sealed class SystemClock : IClock { public DateTime Read() => DateTime.Now; }
                    public interface IProductSource { Product Get(); }
                    public static class Entries
                    {
                        public static readonly IClock Clock = new SystemClock();
                        public static void Log(Product product) { var json = JsonConvert.SerializeObject(product); }
                        public static Product Default() => JsonConvert.DeserializeObject<Product>("{}");
                        public static string FromSource(IProductSource source) => JsonConvert.SerializeObject(source.Get());
                        public static string Stamped() => JsonConvert.SerializeObject(Clock.Read());
                        internal static string Friend(Product product) => JsonConvert.SerializeObject(product);
                        private static string Deep(Product product) => JsonConvert.SerializeObject(product);
                        private static string Deeper(Product product) => Deep(product);
                        public static string Deepest(Product product) => Deeper(product);
                    }
                }
                """),
        });

        var sites = result.Projects.Single(p => p.Project == Catalog).Sites.ToDictionary(s => s.Id);
        Assert.Equal(UncoveredReason.NotObservable, sites["Contoso.Catalog.Entries.Log#1"].Uncovered!.Reason);
        Assert.Equal(UncoveredReason.InputNotParameter, sites["Contoso.Catalog.Entries.Default#1"].Uncovered!.Reason);
        Assert.Equal(UncoveredReason.TypeNotGeneratable, sites["Contoso.Catalog.Entries.FromSource#1"].Uncovered!.Reason);
        var stamped = sites["Contoso.Catalog.Entries.Stamped#1"].Uncovered!;
        Assert.Equal(UncoveredReason.NonHermetic, stamped.Reason);
        Assert.Contains("System.DateTime.Now", stamped.Detail, StringComparison.Ordinal);
        Assert.Equal("M:Contoso.Catalog.Entries.Friend(Contoso.Catalog.Product)", sites["Contoso.Catalog.Entries.Friend#1"].EntryPoint!.Member);
        var deep = sites["Contoso.Catalog.Entries.Deep#1"].Uncovered!;
        Assert.Equal(UncoveredReason.DepthExceeded, deep.Reason);
        Assert.Equal([UncoveredReason.NotAccessible, UncoveredReason.NotAccessible], deep.Candidates.Select(c => c.Reason));
        Assert.All(result.Projects.SelectMany(p => p.Sites), s => Assert.True(s.EntryPoint is null != (s.Uncovered is null)));
    }

    [Fact]
    public async Task Without_findings_the_sites_are_the_same_and_nothing_is_judged()
    {
        // Issue #54: `newtonsoft tests` ran the behavior detectors and threw their findings away.
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (full, _) = Scan(scanned);
        var (sites, _) = Scan(scanned, r => r with { Findings = false });

        Assert.NotEmpty(full.Findings);
        Assert.Empty(sites.Findings);
        Assert.Equal(OfframpJson.Serialize(full with { Findings = [], Summary = full.Summary with { Findings = sites.Summary.Findings } }, AnalysisJsonContext.Default.NewtonsoftScanResult),
            OfframpJson.Serialize(sites, AnalysisJsonContext.Default.NewtonsoftScanResult));
    }

    [Fact]
    public async Task A_site_in_a_test_project_says_its_project_is_a_test_project()
    {
        // Issue #52: "the site's member is not declared in source", for members declared in source.
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var model = scanned.Outcome.Model!;
        var (result, _) = Scan(scanned, r => r with
        {
            Model = model with { Projects = [.. model.Projects.Select(p => p.Id == Catalog ? p with { Kind = ProjectKind.Test, KindEvidence = "PackageReference NUnit" } : p)] },
        });

        var sites = result.Projects.Single(p => p.Project == Catalog).Sites;
        Assert.NotEmpty(sites);
        Assert.All(sites, s =>
        {
            Assert.Null(s.EntryPoint);
            Assert.Equal(UncoveredReason.NotAccessible, s.Uncovered!.Reason);
            Assert.Equal($"{Catalog} is a test project (PackageReference NUnit), and test code is not searched for entry points; if it is production code, set its kind in offramp.yml", s.Uncovered.Detail);
        });
    }

    [Fact]
    public async Task A_public_caller_is_the_entry_point_and_the_depth_bounds_the_search()
    {
        // Reorder.Plan is internal to Inventory; a public caller in Inventory reaches it, and with
        // depth 0 allowed only, the same site has none.
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var source = """
            namespace Contoso.Inventory
            {
                public static class Reorders
                {
                    public static string For(Contoso.Catalog.Product product) => Reorder.Plan(product, 3);
                }
            }
            """;
        var (reached, _) = Scan(scanned, r => r with { Compilations = new AddedSource(r.RepositoryRoot, Inventory, source) });
        var (bounded, _) = Scan(scanned, r => r with { EntryDepth = 0, Compilations = new AddedSource(r.RepositoryRoot, Inventory, source) });

        static NewtonsoftSite Plan(NewtonsoftScanResult result) => result.Projects.Single(p => p.Project == Inventory).Sites.Single(s => s.Id == "Contoso.Inventory.Reorder.Plan#1");
        Assert.Equal("M:Contoso.Inventory.Reorders.For(Contoso.Catalog.Product)", Plan(reached).EntryPoint!.Member);
        Assert.Equal(UncoveredReason.DepthExceeded, Plan(bounded).Uncovered!.Reason);
    }

    [Fact]
    public async Task Settings_are_followed_through_a_lazy_serializer_and_a_field_set_once()
    {
        // MassTransit field test P1 #2: its JSON, BSON, and encrypted message serializers were "unknown".
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var (result, _) = Scan(scanned, r => r with
        {
            Compilations = new AddedSource(r.RepositoryRoot, Catalog, """
                namespace Contoso.Catalog
                {
                    using System;
                    using System.IO;
                    using Newtonsoft.Json;

                    public class Envelope { public string Id { get; set; } }

                    public class LazyWriter
                    {
                        public static JsonSerializerSettings Settings = new JsonSerializerSettings { MaxDepth = 10 };
                        static readonly Lazy<JsonSerializer> _serializer;
                        static LazyWriter() { _serializer = new Lazy<JsonSerializer>(() => JsonSerializer.Create(Settings)); }
                        public static JsonSerializer Serializer => _serializer.Value;
                        public void Write(TextWriter writer, Envelope envelope) => _serializer.Value.Serialize(writer, envelope);
                    }

                    public class CopiedWriter
                    {
                        readonly JsonSerializer _serializer;
                        public CopiedWriter() { _serializer = LazyWriter.Serializer; }
                        public void Write(TextWriter writer, Envelope envelope) => _serializer.Serialize(writer, envelope);
                    }

                    public class InjectedWriter
                    {
                        readonly JsonSerializer _serializer;
                        public InjectedWriter(JsonSerializer serializer) { _serializer = serializer; }
                        public void Write(TextWriter writer, Envelope envelope) => _serializer.Serialize(writer, envelope);
                    }

                    public class TwoConstructors
                    {
                        readonly JsonSerializer _serializer;
                        public TwoConstructors() { _serializer = LazyWriter.Serializer; }
                        public TwoConstructors(int unused) { }
                        public void Write(TextWriter writer, Envelope envelope) => _serializer.Serialize(writer, envelope);
                    }
                }
                """),
        });

        var sites = result.Projects.Single(p => p.Project == Catalog).Sites.ToDictionary(s => s.Id, s => s.Settings);
        Assert.Equal((SettingsKind.Member, "F:Contoso.Catalog.LazyWriter.Settings"), (sites["Contoso.Catalog.LazyWriter.Write#1"].Kind, sites["Contoso.Catalog.LazyWriter.Write#1"].Symbol));
        Assert.Equal((SettingsKind.Member, "P:Contoso.Catalog.LazyWriter.Serializer"), (sites["Contoso.Catalog.CopiedWriter.Write#1"].Kind, sites["Contoso.Catalog.CopiedWriter.Write#1"].Symbol));
        // A serializer passed in, or a field another constructor leaves null, stays unknown.
        Assert.Equal(SettingsKind.Unknown, sites["Contoso.Catalog.InjectedWriter.Write#1"].Kind);
        Assert.Equal(SettingsKind.Unknown, sites["Contoso.Catalog.TwoConstructors.Write#1"].Kind);
    }

    internal static (NewtonsoftScanResult Result, DiagnosticBag Diagnostics) Scan(ScannedFixture scanned, Func<NewtonsoftScanRequest, NewtonsoftScanRequest>? customize = null)
    {
        var diagnostics = new DiagnosticBag();
        var request = new NewtonsoftScanRequest
        {
            RepositoryRoot = scanned.Root,
            Model = scanned.Outcome.Model!,
            Target = Target,
            TargetPackage = File.ReadAllBytes(TargetPackagePath()),
            Diagnostics = diagnostics,
        };
        return (NewtonsoftScanner.Scan(customize?.Invoke(request) ?? request), diagnostics);
    }

    internal static string TargetPackagePath() =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(NewtonsoftReleases.AssemblyPath(Target))))!, "newtonsoft.json.13.0.4.nupkg");

    private static string Render(NewtonsoftScanResult result, DiagnosticBag diagnostics) =>
        OfframpJson.Serialize(result, AnalysisJsonContext.Default.NewtonsoftScanResult) + "\n"
        + string.Join("\n", diagnostics.ToSortedList().Select(d => $"{d.Code} {d.Severity.ToWire()} {d.Project} {d.File}:{d.Line} {d.Message}")) + "\n";

    /// <summary>The model with a project's Newtonsoft.Json release changed (its compilation is left as recorded).</summary>
    private static WorkspaceModel WithVersion(WorkspaceModel model, string project, string version) => model with
    {
        Projects = [.. model.Projects.Select(p => p.Id != project ? p : p with
        {
            PackageReferences = [.. p.PackageReferences.Select(r => r.Id == "Newtonsoft.Json" ? r with { Version = version } : r)],
            Resolved = new SortedDictionary<string, ResolvedFramework>(
                p.Resolved.ToDictionary(r => r.Key, r => r.Value with { Packages = [.. r.Value.Packages.Select(k => k.Id == "Newtonsoft.Json" ? k with { Version = version } : k)] }),
                StringComparer.Ordinal),
        })],
    };

    /// <summary>The recorded compilations, with a source file added to one project's.</summary>
    private sealed class AddedSource(string root, string project, string source) : ICompilationSource, IDisposable
    {
        private readonly CompilationLoader _loader = new(root);

        public Compilation? LoadForProject(ProjectInfo info, string targetFramework)
        {
            var compilation = _loader.LoadForProject(info, targetFramework);
            return info.Id != project || compilation is not CSharpCompilation csharp
                ? compilation
                : csharp.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)csharp.SyntaxTrees.First().Options,
                    Path.Combine(root, Path.GetDirectoryName(project)!, "Added.cs")));
        }

        public (ImmutableArray<DiagnosticAnalyzer> Analyzers, AnalyzerOptions Options)? LoadAnalyzers(ProjectInfo info, string targetFramework) =>
            _loader.LoadAnalyzers(info, targetFramework);

        public void Dispose() => _loader.Dispose();
    }
}
