using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Scanning;
using Offramp.Workspace.Store;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Tests;

/// <summary>ROADMAP M5 acceptance: every case in move-cases yields the specified outcome.</summary>
public sealed class MovePlannerTests
{
    private static readonly string[] Cases =
    [
        "src/Legacy/Clean/Money.cs", "src/Legacy/Json/Serializer.cs", "src/Legacy/Orders/OrderMapper.cs", "src/Legacy/Cycle/Reporter.cs",
        "src/Legacy/Web/LinkBuilder.cs", "src/Legacy/Partial/Invoice.cs", "src/Legacy/Resources/Strings.Designer.cs", "src/Legacy/Internal/Rounding.cs",
    ];

    [Fact]
    [ProducesDiagnostic("OFR2001")]
    [ProducesDiagnostic("OFR2110")]
    public async Task Every_case_yields_its_outcome()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        var diagnostics = new DiagnosticBag();

        var result = Plan(fixture, Cases, diagnostics)!;
        var plan = result.Plan;

        Assert.Equal(
            [
                "src/Legacy/Clean/Money.cs", "src/Legacy/Internal/Rounding.cs", "src/Legacy/Json/Serializer.cs", "src/Legacy/Orders/OrderMapper.cs",
                "src/Legacy/Partial/Invoice.Totals.cs", "src/Legacy/Partial/Invoice.cs", "src/Legacy/Resources/Strings.Designer.cs", "src/Legacy/Resources/Strings.resx",
            ],
            plan.Moves.Select(m => m.File));
        Assert.Equal("src/Core/Clean/Money.cs", plan.Moves[0].To);
        Assert.Equal("src/Legacy/Partial/Invoice.cs", plan.Moves.Single(m => m.File.EndsWith("Invoice.Totals.cs", StringComparison.Ordinal)).CoMoveOf);
        Assert.Equal(ContentHash.Sha256File(fixture.Repository.Directory.Combine("src", "Legacy", "Clean", "Money.cs")), plan.Moves[0].Sha256);
        Assert.Equal([("src/Legacy/Cycle/Reporter.cs", "OFR2001"), ("src/Legacy/Web/LinkBuilder.cs", "OFR2103")], plan.Excluded.Select(e => (e.File, e.Code)));
        Assert.Equal(["src/Core/Core.csproj", "src/Reports/Reports.csproj", "src/Core/Core.csproj"], Assert.Single(plan.Cycles).Path);
        Assert.Equal(
            [
                ("src/Core/Core.csproj", ProjectEditKind.AddProjectReference, "src/Contracts/Contracts.csproj"),
                ("src/Core/Core.csproj", ProjectEditKind.AddPackageReference, "Newtonsoft.Json"),
                ("src/Legacy/Legacy.csproj", ProjectEditKind.AddProjectReference, "src/Core/Core.csproj"),
                ("src/Core/Core.csproj", ProjectEditKind.AddInternalsVisibleTo, "Legacy"),
                ("src/Core/Core.csproj", ProjectEditKind.KeepResourceName, "src/Core/Resources/Strings.resx"),
            ],
            plan.ProjectEdits.Select(e => (e.Project, e.Kind, e.Value!)));
        Assert.Equal("Legacy.Resources.Strings.resources", plan.ProjectEdits.Single(e => e.Kind == ProjectEditKind.KeepResourceName).Version);
        Assert.True(diagnostics.Contains("OFR2110"));
        Assert.Contains("rename from src/Legacy/Resources/Strings.resx", result.Preview, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR2101")]
    public async Task Needed_files_co_move_by_default_and_block_the_move_without_closure()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");

        var closure = Plan(fixture, ["src/Legacy/Orders/OrderMapper.cs"], new DiagnosticBag())!.Plan;
        var none = new DiagnosticBag();
        var alone = Plan(fixture, ["src/Legacy/Orders/OrderMapper.cs"], none, coMove: "none")!.Plan;

        Assert.Equal(["src/Legacy/Clean/Money.cs", "src/Legacy/Orders/OrderMapper.cs"], closure.Moves.Select(m => m.File));
        Assert.Equal("src/Legacy/Orders/OrderMapper.cs", closure.Moves[0].CoMoveOf);
        Assert.Empty(alone.Moves);
        Assert.Equal("OFR2101", Assert.Single(alone.Excluded).Code);
        Assert.True(none.Contains("OFR2101"));
    }

    [Fact]
    [ProducesDiagnostic("OFR2102")]
    public async Task A_framework_only_package_keeps_the_file()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");

        var plan = Plan(fixture, ["src/Legacy/Data/ShopContext.cs"], new DiagnosticBag())!.Plan;

        Assert.Empty(plan.Moves);
        var excluded = Assert.Single(plan.Excluded);
        Assert.Equal("OFR2102", excluded.Code);
        Assert.Contains("EntityFramework", excluded.Message, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR2104")]
    public async Task Code_the_source_keeps_using_cannot_move_above_it()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");

        var plan = Plan(fixture, ["src/Core/Existing/Slug.cs"], new DiagnosticBag(), from: "src/Core/Core.csproj", to: "src/Reports/Reports.csproj")!.Plan;

        Assert.Empty(plan.Moves);
        Assert.Equal(("OFR2104", "src/Core/Existing/Paths.cs"), (Assert.Single(plan.Excluded).Code, plan.Excluded[0].Details[0]));
    }

    [Fact]
    [ProducesDiagnostic("OFR2105")]
    [ProducesDiagnostic("OFR2111")]
    public async Task Windows_only_apis_warn_and_removed_paths_keep_the_file()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        var diagnostics = new DiagnosticBag();

        var plan = Plan(fixture, ["src/Legacy/Platform/RegistryReader.cs", "src/Legacy/Generated/Stamp.cs"], diagnostics, to: "src/Modern/Modern.csproj")!.Plan;

        Assert.Equal(["src/Legacy/Platform/RegistryReader.cs"], plan.Moves.Select(m => m.File));
        Assert.Equal(("src/Legacy/Generated/Stamp.cs", "OFR2111"), (Assert.Single(plan.Excluded).File, plan.Excluded[0].Code));
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR2105" && d.File == "src/Legacy/Platform/RegistryReader.cs");
    }

    [Fact]
    [ProducesDiagnostic("OFR2120")]
    public async Task Namespace_mismatches_warn_or_block()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        var warn = new DiagnosticBag();

        var warned = Plan(fixture, ["src/Legacy/Clean/Money.cs"], warn, namespaces: "warn")!.Plan;
        var blocked = Plan(fixture, ["src/Legacy/Clean/Money.cs"], new DiagnosticBag(), namespaces: "block")!.Plan;

        Assert.Single(warned.Moves);
        Assert.True(warn.Contains("OFR2120"));
        Assert.Empty(blocked.Moves);
        Assert.Equal("OFR2120", Assert.Single(blocked.Excluded).Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR2003")]
    [ProducesDiagnostic("OFR2004")]
    public async Task Frozen_projects_and_unknown_files_are_refused()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");
        var frozen = new DiagnosticBag();
        var unknown = new DiagnosticBag();

        var config = new OfframpConfig { Projects = [new ProjectOverride { Path = "src/Core/Core.csproj", Frozen = true }] };
        Assert.Null(Plan(fixture, ["src/Legacy/Clean/Money.cs"], frozen, config: config));
        Assert.Null(Plan(fixture, ["src/Legacy/Nope.cs"], unknown));

        Assert.True(frozen.Contains("OFR2003"));
        Assert.True(unknown.Contains("OFR2004"));
    }

    [Fact]
    public async Task All_plans_every_file_of_the_source()
    {
        var fixture = await ScannedFixtures.GetAsync("move-cases");

        var plan = MovePlanner.Plan(Request(fixture, [], new DiagnosticBag()) with { All = true })!.Plan;

        Assert.Contains(plan.Moves, m => m.File == "src/Legacy/Internal/Billing.cs");
        Assert.DoesNotContain(plan.ProjectEdits, e => e.Kind == ProjectEditKind.AddInternalsVisibleTo);
        Assert.Equal(
            ["src/Legacy/Cycle/Reporter.cs", "src/Legacy/Data/ShopContext.cs", "src/Legacy/Platform/RegistryReader.cs", "src/Legacy/Web/LinkBuilder.cs"],
            plan.Excluded.Select(e => e.File));
    }

    [Fact]
    [ProducesDiagnostic("OFR2112")]
    public async Task The_destinations_policy_is_not_portability_and_an_inert_InternalsVisibleTo_item_moves_nothing()
    {
        // As in DotNetNuke: the destination treats a warning as an error, and a shared SolutionInfo.cs
        // turned GenerateAssemblyInfo off, so an InternalsVisibleTo item would be ignored.
        var fixture = await ScannedFixtures.ScanAsync("move-cases", (root, request) =>
        {
            var core = Path.Combine(root, "src", "Core", "Core.csproj");
            File.WriteAllText(core, File.ReadAllText(core).Replace("<RootNamespace>Core</RootNamespace>",
                "<RootNamespace>Core</RootNamespace>\n    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n    <WarningsAsErrors>CS0168</WarningsAsErrors>", StringComparison.Ordinal));
            Directory.CreateDirectory(Path.Combine(root, "src", "Legacy", "Warnings"));
            File.WriteAllText(Path.Combine(root, "src", "Legacy", "Warnings", "Unused.cs"),
                "namespace Legacy.Warnings\n{\n    public static class Unused\n    {\n        public static void Run()\n        {\n            int x;\n        }\n    }\n}\n");
            return request;
        });
        using var _ = fixture.Repository;

        var warnings = new DiagnosticBag();
        var policy = Plan(fixture, ["src/Legacy/Warnings/Unused.cs"], warnings)!.Plan;
        var internals = Plan(fixture, ["src/Legacy/Internal/Rounding.cs"], new DiagnosticBag())!.Plan;

        var excluded = Assert.Single(policy.Excluded);
        Assert.Equal("OFR2112", excluded.Code);
        Assert.Equal("Compiles in src/Core/Core.csproj, which treats warnings as errors (CS0168): netstandard2.0: CS0168: The variable 'x' is declared but never used", excluded.Message);
        Assert.True(warnings.Contains("OFR2112"));
        Assert.Empty(internals.Moves);
        var rounding = Assert.Single(internals.Excluded);
        Assert.Equal("OFR2103", rounding.Code);
        Assert.Equal("Code staying in src/Legacy/Legacy.csproj uses its internal members, and src/Core/Core.csproj does not generate its assembly info (GenerateAssemblyInfo=false), so an InternalsVisibleTo item would have no effect.", rounding.Message);
        Assert.DoesNotContain(internals.ProjectEdits, e => e.Kind == ProjectEditKind.AddInternalsVisibleTo);
    }

    [Fact]
    [ProducesDiagnostic("OFR2113")]
    public async Task Co_moves_stay_when_the_file_they_were_for_stays()
    {
        // As in SmartStoreNET: a requested file that does not compile in the destination had
        // brought along what it needs, and what that needs; none of it moves on its own.
        var fixture = await Extended.Value;
        var diagnostics = new DiagnosticBag();

        var alone = Plan(fixture, ["src/Legacy/Chain/Alpha.cs"], diagnostics)!.Plan;
        var shared = Plan(fixture, ["src/Legacy/Chain/Alpha.cs", "src/Legacy/Chain/Zeta.cs"], new DiagnosticBag())!.Plan;

        Assert.Empty(alone.Moves);
        Assert.Equal(
            [("src/Legacy/Chain/Alpha.cs", "OFR2103"), ("src/Legacy/Chain/Title.cs", "OFR2113"), ("src/Legacy/Chain/Trim.cs", "OFR2113")],
            alone.Excluded.Select(e => (e.File, e.Code)));
        Assert.Equal("Co-moved for src/Legacy/Chain/Alpha.cs, which stays.", alone.Excluded[1].Message);
        Assert.Equal(["src/Legacy/Chain/Title.cs"], alone.Excluded[2].Details);
        Assert.Equal(2, diagnostics.ToSortedList().Count(d => d.Code == "OFR2113"));
        Assert.DoesNotContain(alone.ProjectEdits, e => e.Kind == ProjectEditKind.AddProjectReference);

        // A co-move another moving file still needs stays in the plan, attributed to that file.
        Assert.Equal(
            [("src/Legacy/Chain/Title.cs", "src/Legacy/Chain/Zeta.cs"), ("src/Legacy/Chain/Trim.cs", "src/Legacy/Chain/Title.cs"), ("src/Legacy/Chain/Zeta.cs", null)],
            shared.Moves.Select(m => (m.File, m.CoMoveOf)));
        Assert.Equal(("src/Legacy/Chain/Alpha.cs", "OFR2103"), (Assert.Single(shared.Excluded).File, shared.Excluded[0].Code));

        // A file whose co-move --namespace-mismatch block keeps stays too.
        var blocked = Plan(fixture, ["src/Legacy/Chain/Banner.cs"], new DiagnosticBag(), namespaces: "block")!.Plan;
        Assert.Empty(blocked.Moves);
        Assert.Equal(
            [("src/Legacy/Chain/Banner.cs", "OFR2101"), ("src/Legacy/Chain/Title.cs", "OFR2120"), ("src/Legacy/Chain/Trim.cs", "OFR2120")],
            blocked.Excluded.Select(e => (e.File, e.Code)));
    }

    [Theory]
    [InlineData("Framework")]
    [InlineData("Framework48")]
    public async Task A_framework_source_gets_the_standard_facades_a_build_adds(string name)
    {
        // As in Open Live Writer: the source (net461, or net48) references no .NET Standard
        // assembly yet, so its recorded compilation has no netstandard.dll. The build adds it
        // (and the System.* facades) once the source references the .NET Standard destination.
        var fixture = await Extended.Value;
        var project = $"src/{name}/{name}.csproj";

        var plan = Plan(fixture, [$"src/{name}/Clock.cs"], new DiagnosticBag(), from: project, to: "src/Contracts/Contracts.csproj")!.Plan;

        Assert.Empty(plan.Excluded);
        Assert.Equal([$"src/{name}/Clock.cs"], plan.Moves.Select(m => m.File));
        Assert.Contains(plan.ProjectEdits, e => e.Project == project && e.Kind == ProjectEditKind.AddProjectReference && e.Value == "src/Contracts/Contracts.csproj");
    }

    [Fact]
    public async Task A_framework_destination_gets_the_standard_facades_for_a_reference_the_move_adds()
    {
        // OrderMapper uses Contracts (netstandard2.0), which Framework (net461) does not reference yet.
        var fixture = await Extended.Value;

        var plan = Plan(fixture, ["src/Legacy/Orders/OrderMapper.cs"], new DiagnosticBag(), to: "src/Framework/Framework.csproj")!.Plan;

        Assert.Empty(plan.Excluded);
        Assert.Equal(["src/Legacy/Clean/Money.cs", "src/Legacy/Orders/OrderMapper.cs"], plan.Moves.Select(m => m.File));
        Assert.Contains(plan.ProjectEdits, e => e.Project == "src/Framework/Framework.csproj" && e.Kind == ProjectEditKind.AddProjectReference && e.Value == "src/Contracts/Contracts.csproj");
    }

    [Fact]
    public void A_source_that_breaks_without_the_moved_files_is_reported_once()
    {
        // As in DotNetNuke 9.13 (--all: 98 x OFR2104) and NHibernate 4.1.2: the source keeps a generated half of a
        // partial type whose other half moves, so it no longer compiles. Every file stays, each with the reason; the
        // failure is the project's, so it is one diagnostic, and the partial sibling that stays with its part adds none.
        using var root = new ScratchDirectory("source-breaks");
        string Tree(string path) => root.Combine([.. path.Split('/')]);
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText("namespace Lib { public partial class Widget { public int Size => 1; } }", path: Tree("src/Lib/Widget.cs")),
            CSharpSyntaxTree.ParseText("namespace Lib { public partial class Widget { public int Twice => Size * 2; } }", path: Tree("src/Lib/obj/Widget.g.cs")),
            CSharpSyntaxTree.ParseText("namespace Lib { public static class Clock { public static int Now() => 0; } }", path: Tree("src/Lib/Clock.cs")),
            CSharpSyntaxTree.ParseText("namespace Lib { public partial class Order { public int Id => 1; } }", path: Tree("src/Lib/Parts/Order.cs")),
            CSharpSyntaxTree.ParseText("namespace Lib { public partial class Order { public int Lines => 2; } }", path: Tree("src/Lib/Parts/Order.Lines.cs")),
            CSharpSyntaxTree.ParseText("namespace Lib { public static class User { public static int Get() => new Widget().Twice + Clock.Now() + new Order().Lines; } }", path: Tree("src/Lib/User.cs")),
        };
        MetadataReference[] corlib = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var compilations = new Compilations(CSharpCompilation.Create("Lib", trees, corlib, options));
        var source = new ProjectInfo
        {
            Id = "src/Lib/Lib.csproj", Name = "Lib", SdkStyle = true, TargetFrameworks = ["netstandard2.0"],
            Compile = ["src/Lib/Clock.cs", "src/Lib/Parts/Order.Lines.cs", "src/Lib/Parts/Order.cs", "src/Lib/User.cs", "src/Lib/Widget.cs"],
            CompilerCalls = new(StringComparer.Ordinal) { ["netstandard2.0"] = new CompilerCallRef(".offramp/build.complog", "src/Lib/Lib.csproj", "netstandard2.0") },
        };
        var created = new ProjectInfo { Id = "src/Lib.Core/Lib.Core.csproj", Name = "Lib.Core", SdkStyle = true, TargetFrameworks = ["netstandard2.0"] };
        var model = new WorkspaceModel
        {
            CreatedAt = "2026-09-30T00:00:00Z", RepositoryRoot = root.Path, Projects = [source, created],
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)), Sdk = new SdkInfo("10.0.100", "linux-x64"),
        };
        var diagnostics = new DiagnosticBag();

        var plan = MovePlanner.Plan(new MovePlanRequest
        {
            RepositoryRoot = root.Path, Model = model, Config = new OfframpConfig(), WorkspaceHash = "", From = source.Id, To = created.Id,
            Files = ["src/Lib/Clock.cs", "src/Lib/Parts/Order.cs", "src/Lib/Widget.cs"], CoMove = "closure", NamespaceMismatch = "allow", Diagnostics = diagnostics,
            Create = new NewProject(created, "<Project Sdk=\"Microsoft.NET.Sdk\" />"u8.ToArray(), [("netstandard2.0", CSharpCompilation.Create("Lib.Core", [], corlib, options))]),
            Compilations = compilations,
        })!.Plan;

        Assert.Empty(plan.Moves);
        Assert.Equal(["src/Lib/Clock.cs", "src/Lib/Parts/Order.Lines.cs", "src/Lib/Parts/Order.cs", "src/Lib/Widget.cs"], plan.Excluded.Select(e => e.File));
        Assert.All(plan.Excluded, e => Assert.Equal("OFR2104", e.Code));
        var failure = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR2104");
        Assert.Equal((source.Id, null), (failure.Project, failure.File));
        Assert.StartsWith("Lib does not compile without the 4 files the move would take (CS0103: ", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The recorded compilation of the one source project, and no analyzers.</summary>
    private sealed class Compilations(Compilation source) : Offramp.Analysis.Compilations.ICompilationSource
    {
        public Compilation? LoadForProject(ProjectInfo project, string targetFramework) => project.Id == "src/Lib/Lib.csproj" ? source : null;

        public (System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer> Analyzers, Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions Options)? LoadAnalyzers(ProjectInfo project, string targetFramework) => null;
    }

    /// <summary>
    /// move-cases plus a chain of files in Legacy (Alpha, which uses System.Web's HttpContext, Zeta,
    /// and Banner, in Core's root namespace, need Title, which needs Trim), and two .NET Framework
    /// projects that reference no .NET Standard assembly: Framework (net461) and Framework48 (net48),
    /// where Timer uses Clock.
    /// </summary>
    private static readonly Lazy<Task<ScannedFixture>> Extended = new(async () =>
    {
        var fixture = await ScannedFixtures.ScanAsync("move-cases", Extend);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => fixture.Repository.Dispose();
        return fixture;
    });

    private static ScanRequest Extend(string root, ScanRequest request)
    {
        static void Write(string root, string path, string text)
        {
            var absolute = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }

        Write(root, "src/Legacy/Chain/Alpha.cs", "namespace Legacy.Chain\n{\n    public static class Alpha\n    {\n        public static string Render(string text) => (System.Web.HttpContext.Current?.Request.RawUrl ?? \"/\") + Title.Of(text);\n    }\n}\n");
        Write(root, "src/Legacy/Chain/Zeta.cs", "namespace Legacy.Chain\n{\n    public static class Zeta\n    {\n        public static string Heading(string text) => \"# \" + Title.Of(text);\n    }\n}\n");
        Write(root, "src/Legacy/Chain/Banner.cs", "namespace Core.Chain\n{\n    public static class Banner\n    {\n        public static string Of(string text) => Legacy.Chain.Title.Of(text);\n    }\n}\n");
        Write(root, "src/Legacy/Chain/Title.cs", "namespace Legacy.Chain\n{\n    public static class Title\n    {\n        public static string Of(string text) => Trim.Text(text).ToUpperInvariant();\n    }\n}\n");
        Write(root, "src/Legacy/Chain/Trim.cs", "namespace Legacy.Chain\n{\n    public static class Trim\n    {\n        public static string Text(string text) => text.Trim();\n    }\n}\n");
        foreach (var (name, tfm) in new[] { ("Framework", "net461"), ("Framework48", "net48") })
        {
            Write(root, $"src/{name}/{name}.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{tfm}</TargetFramework>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n");
            Write(root, $"src/{name}/Clock.cs", $"namespace {name}\n{{\n    public static class Clock\n    {{\n        public static System.DateTime Now() => System.DateTime.UtcNow;\n    }}\n}}\n");
            Write(root, $"src/{name}/Timer.cs", $"namespace {name}\n{{\n    public static class Timer\n    {{\n        public static long Ticks() => Clock.Now().Ticks;\n    }}\n}}\n");
        }

        var solution = Path.Combine(root, "MoveCases.slnx");
        File.WriteAllText(solution, File.ReadAllText(solution).Replace(
            "</Folder>", "  <Project Path=\"src/Framework/Framework.csproj\" />\n    <Project Path=\"src/Framework48/Framework48.csproj\" />\n  </Folder>", StringComparison.Ordinal));
        return request;
    }


    private static MovePlanResult? Plan(
        ScannedFixture fixture, IReadOnlyList<string> files, DiagnosticBag diagnostics, string coMove = "closure", string namespaces = "allow",
        string from = "src/Legacy/Legacy.csproj", string to = "src/Core/Core.csproj", OfframpConfig? config = null) =>
        MovePlanner.Plan(Request(fixture, files, diagnostics) with
        {
            From = from, To = to, CoMove = coMove, NamespaceMismatch = namespaces, Config = config ?? new OfframpConfig(),
        });

    private static MovePlanRequest Request(ScannedFixture fixture, IReadOnlyList<string> files, DiagnosticBag diagnostics) => new()
    {
        RepositoryRoot = fixture.Root,
        Model = WorkspaceStore.Read(fixture.WorkspacePath),
        Config = new OfframpConfig(),
        WorkspaceHash = "sha256:" + ContentHash.Sha256File(fixture.WorkspacePath),
        From = "src/Legacy/Legacy.csproj",
        To = "src/Core/Core.csproj",
        Files = files,
        CoMove = "closure",
        NamespaceMismatch = "allow",
        Diagnostics = diagnostics,
    };
}
