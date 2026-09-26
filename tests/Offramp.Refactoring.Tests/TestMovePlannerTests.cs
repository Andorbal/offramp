using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;

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
        Assert.Contains(result.ProjectEdits, e => e.Kind == ProjectEditKind.AddInternalsVisibleTo && e.Value == "Foo.Tests");
        Assert.Empty(result.Prunable);
        Assert.Equal(2, plan.ChangeSet!.Renames.Count);
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

    private static Task<MoveTestsPlan?> Plan(ScannedFixture fixture, string source, DiagnosticBag diagnostics, bool create = false) =>
        TestMovePlanner.PlanAsync(new MoveTestsRequest
        {
            RepositoryRoot = fixture.Root,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            Config = new OfframpConfig(),
            Source = source,
            Create = create,
            Diagnostics = diagnostics,
        }, TestContext.Current.CancellationToken);
}
