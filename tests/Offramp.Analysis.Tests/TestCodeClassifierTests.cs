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

    private static IReadOnlyList<ClassifiedFile> Classify(CSharpCompilation source, Dictionary<string, string> files, IReadOnlyList<ConsumerCompilation> consumers, string destination) =>
        TestCodeClassifier.Classify(source, files, consumers, destination);

    private static readonly MetadataReference Corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    private static (TestFileKind, TestConfidence?) Pair(ClassifiedFile file) => (file.Kind, file.Confidence);
}
