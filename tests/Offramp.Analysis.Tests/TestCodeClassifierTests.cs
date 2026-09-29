using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.TestCode;
using Offramp.Core.Paths;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Analysis.Tests;

public sealed class TestCodeClassifierTests
{
    [Fact]
    public async Task Tests_helpers_and_shared_code_are_told_apart()
    {
        var fixture = await ScannedFixtures.GetAsync("tests-in-prod");
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        using var loader = new CompilationLoader(fixture.Root);
        var foo = model.Projects.Single(p => p.Name == "Foo");
        var tests = model.Projects.Single(p => p.Name == "Foo.Tests");
        var files = foo.Compile.ToDictionary(f => f, f => RepoPaths.ToAbsolute(fixture.Root, f));

        var classified = TestCodeClassifier.Classify(
            loader.LoadForProject(foo)!, files, [new ConsumerCompilation(tests.Id, loader.LoadForProject(tests)!)], tests.Id)
            .ToDictionary(c => c.File);

        Assert.Equal((TestFileKind.Test, TestConfidence.Certain), Pair(classified["src/Foo/Service/Tests/OrderServiceTests.cs"]));
        Assert.Equal(["xunit"], classified["src/Foo/Service/Tests/OrderServiceTests.cs"].Frameworks);
        Assert.Equal((TestFileKind.Helper, TestConfidence.High), Pair(classified["src/Foo/TestData/Builders.cs"]));
        Assert.Equal((TestFileKind.Helper, TestConfidence.Medium), Pair(classified["src/Foo/Testing/FakeClock.cs"]));
        Assert.Equal(TestFileKind.Production, classified["src/Foo/Shared/Clock.cs"].Kind);
        Assert.Equal(["src/Foo/Service/OrderService.cs", "src/Foo/Testing/FakeClock.cs"], classified["src/Foo/Shared/Clock.cs"].ProductionReferrers);
        Assert.Equal(TestFileKind.Production, classified["src/Foo/Service/OrderService.cs"].Kind);
    }

    [Fact]
    public void A_helper_another_project_uses_is_kept_for_it()
    {
        var (source, files) = Source(
            ("src/Foo/Tests/ATests.cs", "[Xunit.Fact] public class ATests { public void T() { new FakeRepo(); } }"),
            ("src/Foo/Testing/FakeRepo.cs", "public class FakeRepo { }"));
        var other = Consumer("Bar", source, "public class BarTests { FakeRepo r = new FakeRepo(); }");

        var toTests = Classify(source, files, [other], destination: "Bar").ToDictionary(c => c.File);
        var elsewhere = Classify(source, files, [other], destination: "Foo.Tests").ToDictionary(c => c.File);

        Assert.Equal((TestFileKind.Helper, TestConfidence.High), Pair(toTests["src/Foo/Testing/FakeRepo.cs"]));
        Assert.Equal(["Bar"], elsewhere["src/Foo/Testing/FakeRepo.cs"].ProductionReferrers);
    }

    [Fact]
    public void A_helper_named_in_a_string_is_only_medium()
    {
        var (source, files) = Source(
            ("src/Foo/Tests/ATests.cs", "[Xunit.Fact] public class ATests { public void T() { new FakeRepo(); } }"),
            ("src/Foo/Testing/FakeRepo.cs", "public class FakeRepo { }"),
            ("src/Foo/Loader.cs", "public static class Loader { public static System.Type T => System.Type.GetType(\"FakeRepo\"); }"));

        var helper = Classify(source, files, [], "Foo.Tests").Single(c => c.File == "src/Foo/Testing/FakeRepo.cs");

        Assert.Equal((TestFileKind.Helper, TestConfidence.Medium), Pair(helper));
        Assert.Contains(helper.Reasons, r => r.Contains("named in a string", StringComparison.Ordinal));
    }

    [Fact]
    public void Code_only_tests_use_is_the_code_under_test_without_evidence()
    {
        var (source, files) = Source(
            ("src/Foo/Tests/ParserTests.cs", "[Xunit.Fact] public class ParserTests { public void T() { new Parser(); } }"),
            ("src/Foo/Parser.cs", "public class Parser { }"));

        var parser = Classify(source, files, [], "Foo.Tests").Single(c => c.File == "src/Foo/Parser.cs");

        Assert.Equal(TestFileKind.Production, parser.Kind);
        Assert.Empty(parser.ProductionReferrers);
    }

    [Fact]
    public void A_public_type_of_a_shipped_library_is_never_test_support()
    {
        var (source, files) = Source(
            ("src/Foo/Tests/QueryTests.cs", "[Xunit.Fact] public class QueryTests { public void T() { Foo.Criterion.QueryOverBuilderExtensions.Eager(1); new Foo.Testing.FakeRepo(); } }"),
            ("src/Foo/Criterion/QueryOverBuilderExtensions.cs", "namespace Foo.Criterion { public static class QueryOverBuilderExtensions { public static int Eager(int x) => x; } }"),
            ("src/Foo/Testing/FakeRepo.cs", "namespace Foo.Testing { public class FakeRepo { } }"));

        var shipped = Classify(source, files, [], "Foo.Test", shipped: "packed by src/Foo/Foo.nuspec.template").ToDictionary(c => c.File);
        var unshipped = Classify(source, files, [], "Foo.Test").ToDictionary(c => c.File);

        // NHibernate's QueryOver API: public, used only by tests in the repository, and named "Builder".
        // The name says nothing about a public type outside a test namespace: it is the code under test.
        Assert.Equal(TestFileKind.Production, shipped["src/Foo/Criterion/QueryOverBuilderExtensions.cs"].Kind);
        Assert.Equal(TestFileKind.Production, unshipped["src/Foo/Criterion/QueryOverBuilderExtensions.cs"].Kind);

        // Test support by folder and name, but public in a shipped library: listed at low, never moved.
        var fake = shipped["src/Foo/Testing/FakeRepo.cs"];
        Assert.Equal((TestFileKind.Helper, TestConfidence.Low), Pair(fake));
        Assert.True(fake.ShippedApi);
        Assert.Equal("public API of a shipped library (packed by src/Foo/Foo.nuspec.template): other repositories may use FakeRepo, so it is never moved", fake.Reasons[0]);
        Assert.Equal((TestFileKind.Helper, TestConfidence.High), Pair(unshipped["src/Foo/Testing/FakeRepo.cs"]));
        Assert.False(unshipped["src/Foo/Testing/FakeRepo.cs"].ShippedApi);
    }

    [Theory]
    [InlineData("internal class OrderBuilder { }", true)]
    [InlineData("internal class FakesRegistry { }", true)]
    [InlineData("internal class Stubborn { }", false)]
    [InlineData("internal class Mockingbird { }", false)]
    [InlineData("public class LinqContainsPredicateBuilder { }", false)]
    [InlineData("public class LocalizationExpressionBuilder { }", false)]
    [InlineData("namespace Foo.TestData { public class OrderBuilder { } }", true)]
    [InlineData("namespace Foo.UnitTests { public class ClockStub { } }", true)]
    public void A_name_says_test_support_as_a_whole_word_of_a_type_that_is_not_public_api(string declaration, bool hinted)
    {
        var (source, files) = Source(
            ("src/Foo/Tests/UseTests.cs", "[Xunit.Fact] public class UseTests { }"),
            ("src/Foo/Support.cs", declaration));

        var support = Classify(source, files, [], "Foo.Tests").Single(c => c.File == "src/Foo/Support.cs");

        Assert.Equal(hinted, support.Reasons.Any(r => r.StartsWith("name suggests test support", StringComparison.Ordinal)));
    }

    private static (CSharpCompilation, Dictionary<string, string>) Source(params (string File, string Code)[] files)
    {
        var xunit = CSharpSyntaxTree.ParseText("namespace Xunit { public class FactAttribute : System.Attribute { } }");
        var framework = CSharpCompilation.Create("xunit.core", [xunit], [Corlib], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Code, path: "/r/" + f.File)).ToList();
        return (
            CSharpCompilation.Create("Foo", trees, [Corlib, framework.ToMetadataReference()], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
            files.ToDictionary(f => f.File, f => "/r/" + f.File));
    }

    private static ConsumerCompilation Consumer(string name, CSharpCompilation source, string code) =>
        new(name, CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code, path: $"/r/src/{name}/{name}.cs")], [Corlib, source.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static IReadOnlyList<ClassifiedFile> Classify(
        CSharpCompilation source, Dictionary<string, string> files, IReadOnlyList<ConsumerCompilation> consumers, string destination, string? shipped = null) =>
        TestCodeClassifier.Classify(source, files, consumers, destination, shipped);

    private static readonly MetadataReference Corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    private static (TestFileKind, TestConfidence?) Pair(ClassifiedFile file) => (file.Kind, file.Confidence);
}
