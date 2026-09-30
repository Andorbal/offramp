using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.TestCode;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Tests;

public sealed class TestMovePlannerTests
{
    [Fact]
    public async Task Foo_plan_moves_tests_and_helpers_and_explains_the_rest()
    {
        var fixture = await ScannedFixtures.GetAsync("tests-in-prod");
        var diagnostics = new DiagnosticBag();

        var plan = await Plan(fixture, "src/Foo/Foo.csproj", diagnostics);

        var result = plan!.Result;
        Assert.Equal("src/Foo.Tests/Foo.Tests.csproj", result.To);
        Assert.Equal(
            [
                ("src/Foo/Service/Tests/OrderServiceTests.cs", "src/Foo.Tests/Service/OrderServiceTests.cs"),
                ("src/Foo/TestData/Builders.cs", "src/Foo.Tests/TestData/Builders.cs"),
            ],
            result.Moves.Select(m => (m.File, m.To)));
        Assert.Equal(
            [("src/Common/SharedTests.cs", "OFR2206"), ("src/Foo/Health/StartupChecks.cs", "OFR2201"), ("src/Foo/Web/Tests/UrlTests.cs", "OFR2103")],
            result.Skipped.Select(s => (s.File, s.Code)));
        Assert.Equal(["src/Foo/Testing/FakeClock.cs"], result.Candidates.Select(c => c.File));

        // Foo is a library no application uses, so its public FakeClock may be someone's API: low, and it stays.
        Assert.Equal(TestConfidence.Low, result.Candidates[0].Confidence);
        Assert.Equal(
            "public API of a shipped library (no application in the solution uses it): other repositories may use FakeClock, so it is never moved",
            result.Candidates[0].Reasons[0]);
        Assert.Contains(result.ProjectEdits, e => e.Kind == ProjectEditKind.AddInternalsVisibleTo && e.Value == "Foo.Tests");
        Assert.Empty(result.Prunable);
        Assert.Equal(2, plan.ChangeSet!.Renames.Count);
    }

    [Fact]
    public async Task Public_api_of_a_shipped_library_never_moves_even_with_low_helpers()
    {
        var fixture = await ScannedFixtures.GetAsync("tests-in-prod");

        var plan = await Plan(fixture, "src/Foo/Foo.csproj", new DiagnosticBag(), helpers: TestConfidence.Low);

        Assert.DoesNotContain(plan!.Result.Moves, m => m.File == "src/Foo/Testing/FakeClock.cs");
        Assert.Contains("src/Foo/TestData/Builders.cs", plan.Result.Moves.Select(m => m.File));
        Assert.Equal(["src/Foo/Testing/FakeClock.cs"], plan.Result.Candidates.Select(c => c.File));
    }

    [Fact]
    [ProducesDiagnostic("OFR2204")]
    public async Task A_taken_destination_path_keeps_the_file()
    {
        var fixture = await ScannedFixtures.ScanAsync("tests-in-prod");
        using var repository = fixture.Repository;
        repository.Directory.Write("src/Foo.Tests/TestData/Builders.cs", "// already here\n");
        var diagnostics = new DiagnosticBag();

        var plan = await Plan(fixture, "src/Foo/Foo.csproj", diagnostics);

        Assert.Equal(["src/Foo/Service/Tests/OrderServiceTests.cs"], plan!.Result.Moves.Select(m => m.File));
        Assert.Contains(plan.Result.Skipped, s => s.File == "src/Foo/TestData/Builders.cs" && s.Code == "OFR2204");
        Assert.True(diagnostics.Contains("OFR2204"));
    }

    [Fact]
    [ProducesDiagnostic("OFR2104")]
    public void Nothing_moves_when_the_source_would_stop_compiling()
    {
        // Production code converts a builder implicitly: no name in it refers to the operator, so only compiling tells.
        var production = CSharpSyntaxTree.ParseText(
            "public class Order { } public static class Shop { public static Order Make() { Order o = new OrderBuilder(); return o; } }", path: "/r/src/Foo/Shop.cs");
        var helper = CSharpSyntaxTree.ParseText(
            "public class OrderBuilder { public static implicit operator Order(OrderBuilder b) => new Order(); }", path: "/r/src/Foo/TestData/OrderBuilder.cs");
        var compilation = CSharpCompilation.Create("Foo", [production, helper], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal) { ["src/Foo/TestData/OrderBuilder.cs"] = helper };
        var diagnostics = new DiagnosticBag();
        var skipped = new List<SkippedFile>();

        TestMovePlanner.KeepIfSourceBreaks(compilation, trees, "src/Foo/Foo.csproj", diagnostics, skipped);

        Assert.Empty(trees);
        Assert.Equal("OFR2104", Assert.Single(skipped).Code);
        Assert.StartsWith("CS0246", skipped[0].Details[0], StringComparison.Ordinal);
        Assert.True(diagnostics.Contains("OFR2104"));
    }

    [Fact]
    public void A_source_that_breaks_is_reported_once_with_every_file_kept()
    {
        var production = CSharpSyntaxTree.ParseText(
            "public class Order { } public static class Shop { public static Order Make() { Order o = new OrderBuilder(); return o; } }", path: "/r/src/Foo/Shop.cs");
        var helper = CSharpSyntaxTree.ParseText(
            "public class OrderBuilder { public static implicit operator Order(OrderBuilder b) => new Order(); }", path: "/r/src/Foo/TestData/OrderBuilder.cs");
        var test = CSharpSyntaxTree.ParseText("public class OrderTests { OrderBuilder b = new OrderBuilder(); }", path: "/r/src/Foo/Tests/OrderTests.cs");
        var compilation = CSharpCompilation.Create("Foo", [production, helper, test], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal) { ["src/Foo/TestData/OrderBuilder.cs"] = helper, ["src/Foo/Tests/OrderTests.cs"] = test };
        var diagnostics = new DiagnosticBag();
        var skipped = new List<SkippedFile>();

        TestMovePlanner.KeepIfSourceBreaks(compilation, trees, "src/Foo/Foo.csproj", diagnostics, skipped);

        // Each file says why it stays; the project-level failure is one diagnostic (NHibernate had 1,272 identical ones).
        Assert.Equal(["src/Foo/TestData/OrderBuilder.cs", "src/Foo/Tests/OrderTests.cs"], skipped.Select(s => s.File));
        var failure = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR2104");
        Assert.Equal("src/Foo/Foo.csproj", failure.Project);
        Assert.Null(failure.File);
        Assert.StartsWith("Foo does not compile without the 2 files move tests would take (CS0246: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_project_named_after_the_source_a_test_project_that_references_it_is_the_destination()
    {
        var source = Project("src/NHibernate/NHibernate.csproj", ProjectKind.Library);
        var test = Project("src/NHibernate.Test/NHibernate.Test.csproj", ProjectKind.Test, source.Id);
        var setup = Project("src/NHibernate.TestDatabaseSetup/NHibernate.TestDatabaseSetup.csproj", ProjectKind.Test, source.Id);
        var vb = Project("src/NHibernate.Test.VisualBasic/NHibernate.Test.VisualBasic.vbproj", ProjectKind.Test, source.Id) with { Language = "vb" };
        var model = Model(source, test, setup, vb, Project("src/NHibernate.DomainModel/NHibernate.DomainModel.csproj", ProjectKind.Library, source.Id));

        // Of the two C# test projects, the one named after the source; the Visual Basic one cannot take C# files.
        var chosen = TestTargets.Select(model, source, to: null, create: false, ".Tests");
        Assert.Equal((test.Id, true, false), (chosen.Project, chosen.ByReference, chosen.Create));
        Assert.Equal([test.Id, setup.Id], chosen.Referencing);

        // The only one that references the source, whatever its name.
        var only = TestTargets.Select(Model(source, setup), source, to: null, create: false, ".Tests");
        Assert.Equal(setup.Id, only.Project);

        // Two, neither named after the source: ambiguous.
        var integration = Project("src/NHibernate.Integration/NHibernate.Integration.csproj", ProjectKind.Test, source.Id);
        var ambiguous = TestTargets.Select(Model(source, setup, integration), source, to: null, create: false, ".Tests");
        Assert.Null(ambiguous.Project);
        Assert.Equal([integration.Id, setup.Id], ambiguous.Ambiguous);

        // --create creates, and names the projects that could have taken the tests; a name match still comes first.
        var created = TestTargets.Select(model, source, to: null, create: true, ".Tests");
        Assert.Equal(("src/NHibernate.Tests/NHibernate.Tests.csproj", true), (created.Project, created.Create));
        Assert.Equal([test.Id, setup.Id], created.Referencing);
        Assert.Equal(test.Id, TestTargets.Select(model, source, to: null, create: true, ".Test").Project);
        Assert.Null(TestTargets.Select(Model(source), source, to: null, create: false, ".Tests").Project);
    }

    [Fact]
    public void Nothing_is_kept_back_when_the_source_still_compiles()
    {
        var production = CSharpSyntaxTree.ParseText("public class Order { }", path: "/r/src/Foo/Order.cs");
        var test = CSharpSyntaxTree.ParseText("public class OrderTests { Order o = new Order(); }", path: "/r/src/Foo/OrderTests.cs");
        var compilation = CSharpCompilation.Create("Foo", [production, test], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal) { ["src/Foo/OrderTests.cs"] = test };

        TestMovePlanner.KeepIfSourceBreaks(compilation, trees, "src/Foo/Foo.csproj", new DiagnosticBag(), []);

        Assert.Single(trees);
    }

    [Theory]
    [InlineData("src/Foo/Service/Tests/X.cs", true, "src/Foo.Tests/Service/X.cs")]
    [InlineData("src/Foo/Service/Tests/X.cs", false, "src/Foo.Tests/Service/Tests/X.cs")]
    [InlineData("src/Foo/Tests/Deep/Tests/X.cs", true, "src/Foo.Tests/Deep/Tests/X.cs")]
    [InlineData("src/Foo/Tests.cs", true, "src/Foo.Tests/Tests.cs")]
    [InlineData("src/Common/X.cs", true, null)]
    public void Paths_map_under_the_destination_less_a_tests_folder(string file, bool strip, string? expected)
    {
        Assert.Equal(expected, TestTargets.MapPath(file, "src/Foo/Foo.csproj", "src/Foo.Tests/Foo.Tests.csproj", strip));
    }

    [Fact]
    public void A_created_project_sits_next_to_the_source()
    {
        Assert.Equal("src/Bar.Tests/Bar.Tests.csproj", TestTargets.CreatedPath("src/Bar/Bar.csproj", "Bar.Tests"));
        Assert.Equal("Bar.Tests/Bar.Tests.csproj", TestTargets.CreatedPath("Bar/Bar.csproj", "Bar.Tests"));
    }

    private static WorkspaceModel Model(params ProjectInfo[] projects) => new()
    {
        CreatedAt = "2026-09-29T00:00:00Z",
        RepositoryRoot = "/r",
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Projects = projects,
    };

    private static ProjectInfo Project(string id, ProjectKind kind, params string[] references) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(id),
        Kind = kind,
        ProjectReferences = references,
    };

    private static Task<MoveTestsPlan?> Plan(
        ScannedFixture fixture, string source, DiagnosticBag diagnostics, bool create = false, TestConfidence? helpers = TestConfidence.High) =>
        TestMovePlanner.PlanAsync(new MoveTestsRequest
        {
            RepositoryRoot = fixture.Root,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            Config = new OfframpConfig(),
            Source = source,
            Create = create,
            IncludeHelpers = helpers,
            Diagnostics = diagnostics,
        }, TestContext.Current.CancellationToken);
}
