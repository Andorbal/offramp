using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;

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
