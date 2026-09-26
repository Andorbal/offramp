using Offramp.Core.Configuration;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

public sealed class ProjectModelBuilderTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("builder");

    public ProjectModelBuilderTests() => _repo.Write("src/Lib/Lib.csproj", "<Project />");

    public void Dispose() => _repo.Dispose();

    /// <summary>
    /// Logs captured on Windows record target-generated Compile items (AssemblyInfo,
    /// global usings) with the evaluation; they are build output, not sources.
    /// </summary>
    [Fact]
    public void Compile_items_in_intermediate_and_output_directories_are_left_out()
    {
        var project = Build(Evaluation(
            properties: new() { ["BaseIntermediateOutputPath"] = @"obj\", ["IntermediateOutputPath"] = @"obj\Debug\net48\", ["OutputPath"] = @"out\Debug\" },
            compile: ["Code.cs", @"Sub\More.cs", @"obj\Debug\net48\Lib.AssemblyInfo.cs", @"out\Debug\Copied.cs", @"objects\Model.cs"]));

        Assert.Equal(["src/Lib/Code.cs", "src/Lib/Sub/More.cs", "src/Lib/objects/Model.cs"], project.Compile);
    }

    [Fact]
    public void An_output_path_that_holds_the_project_does_not_hide_its_sources()
    {
        var project = Build(Evaluation(
            properties: new() { ["OutputPath"] = @"..\", ["BaseIntermediateOutputPath"] = @".\" },
            compile: ["Code.cs"]));

        Assert.Equal(["src/Lib/Code.cs"], project.Compile);
    }

    [Fact]
    public void Package_injected_and_path_references_are_handled()
    {
        var project = Build(Evaluation(
            properties: [],
            compile: ["Code.cs"],
            references:
            [
                new EvaluatedItem("System.Web", new Dictionary<string, string>()),
                new EvaluatedItem("/home/u/.nuget/packages/netstandard.library/2.0.3/build/netstandard2.0/ref/System.Runtime.dll",
                    new Dictionary<string, string> { ["NuGetPackageId"] = "NETStandard.Library" }),
                new EvaluatedItem(@"..\..\lib\Vendor.Thing.dll", new Dictionary<string, string>()),
                new EvaluatedItem("mscorlib", new Dictionary<string, string>()),
            ]));

        Assert.Equal(["System.Web", "Vendor.Thing"], project.AssemblyReferences.Select(r => r.Name));
        Assert.Equal("lib/Vendor.Thing.dll", project.AssemblyReferences.Single(r => r.Name == "Vendor.Thing").HintPath);
        Assert.Equal(AssemblyReferenceKind.File, project.AssemblyReferences.Single(r => r.Name == "Vendor.Thing").Kind);
    }

    private ProjectInfo Build(EvaluatedProject evaluation) =>
        ProjectModelBuilder.Build("src/Lib/Lib.csproj", [evaluation], new ProjectBuildContext
        {
            Paths = CapturePathMapper.Local(_repo.Path),
            Config = new OfframpConfig(),
            CompilerCalls = new Dictionary<(string, string), CompilerCallRef>(),
            Excluded = new PathGlobs([]),
        });

    private EvaluatedProject Evaluation(Dictionary<string, string> properties, string[] compile, EvaluatedItem[]? references = null)
    {
        properties["TargetFramework"] = "net48";
        properties["UsingMicrosoftNETSdk"] = "true";
        return new EvaluatedProject
        {
            ProjectFile = Path.Combine(_repo.Path, "src", "Lib", "Lib.csproj"),
            TargetFramework = "net48",
            Properties = properties,
            Items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>
            {
                ["Compile"] = [.. compile.Select(c => new EvaluatedItem(c, new Dictionary<string, string>()))],
                ["Reference"] = references ?? [],
            },
        };
    }
}
