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

    /// <summary>
    /// Open Live Writer field test (P0 #4): a HintPath into the NuGet global packages folder, declared
    /// once in Directory.Build.props, kept only its file name and no metadata, so resolve-dlls guessed
    /// its version. It is written $(NuGetPackageRoot)id/version/..., and a DLL outside the repository
    /// keeps its file name but has its metadata read.
    /// </summary>
    [Fact]
    public void Hint_paths_outside_the_repository_keep_their_package_folder_and_metadata()
    {
        using var outside = new ScratchDirectory("nuget");
        var packages = outside.Combine("packages") + "/";
        var dll = outside.Combine("packages", "system.resources.extensions", "6.0.0", "lib", "net461", "System.Resources.Extensions.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, Offramp.Fixtures.Feeds.StubAssembly.Build(new() { Name = "System.Resources.Extensions", Version = "6.0.0.0", PublicKey = "00240000048000009400000006020000" }));
        var vendor = outside.Combine("vendor", "Vendor.Tool.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(vendor)!);
        File.WriteAllBytes(vendor, Offramp.Fixtures.Feeds.StubAssembly.Build(new() { Name = "Vendor.Tool", Version = "2.0.0.0" }));

        var project = Build(Evaluation(
            properties: new() { ["NuGetPackageRoot"] = packages },
            compile: ["Code.cs"],
            references:
            [
                new EvaluatedItem("System.Resources.Extensions", new Dictionary<string, string> { ["HintPath"] = dll }),
                new EvaluatedItem("Vendor.Tool", new Dictionary<string, string> { ["HintPath"] = vendor }),
            ]));

        var extensions = project.AssemblyReferences.Single(r => r.Name == "System.Resources.Extensions");
        Assert.Equal("$(NuGetPackageRoot)system.resources.extensions/6.0.0/lib/net461/System.Resources.Extensions.dll", extensions.HintPath);
        Assert.Equal("6.0.0.0", extensions.Metadata?.AssemblyVersion);
        var tool = project.AssemblyReferences.Single(r => r.Name == "Vendor.Tool");
        Assert.Equal(("Vendor.Tool.dll", "2.0.0.0"), (tool.HintPath, tool.Metadata?.AssemblyVersion));
    }

    /// <summary>
    /// MSBuild gives every legacy project System.Core, declared or not; an evaluation on Windows
    /// already lists it for a project that does not declare it, one on Linux does not.
    /// </summary>
    [Fact]
    public void References_msbuild_adds_to_every_legacy_project_are_implicit()
    {
        var project = Build(Evaluation(
            properties: new() { ["AdditionalExplicitAssemblyReferences"] = "System.Core;" },
            compile: ["Code.cs"],
            references:
            [
                new EvaluatedItem("System", new Dictionary<string, string>()),
                new EvaluatedItem("System.Core", new Dictionary<string, string> { ["Implicit"] = "true" }),
            ]));

        Assert.Equal(["System"], project.AssemblyReferences.Select(r => r.Name));
    }

    /// <summary>
    /// Logs captured on Windows also record the ProjectReference items the SDK adds for
    /// transitive references (IncludeTransitiveProjectReferences); only the references restore
    /// saw declared are the project's.
    /// </summary>
    [Fact]
    public void Transitive_project_references_the_sdk_adds_are_left_out()
    {
        var assets = Path.Combine(_repo.Path, "src", "Lib", "obj", "project.assets.json");
        var declared = Path.Combine(_repo.Path, "src", "A", "A.csproj").Replace("\\", "\\\\");
        _repo.Write("src/Lib/obj/project.assets.json", $$"""
            {
              "version": 3,
              "targets": { ".NETFramework,Version=v4.8": {} },
              "libraries": {},
              "projectFileDependencyGroups": { ".NETFramework,Version=v4.8": [] },
              "project": {
                "version": "1.0.0",
                "restore": {
                  "projectUniqueName": "Lib",
                  "projectName": "Lib",
                  "projectStyle": "PackageReference",
                  "frameworks": { "net48": { "targetAlias": "net48", "projectReferences": { "{{declared}}": { "projectPath": "{{declared}}" } } } }
                },
                "frameworks": { "net48": { "targetAlias": "net48" } }
              }
            }
            """);
        var evaluation = Evaluation(new() { ["ProjectAssetsFile"] = assets }, []);
        evaluation = evaluation with
        {
            Items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>(evaluation.Items)
            {
                ["ProjectReference"] = [new(@"..\A\A.csproj", new Dictionary<string, string>()), new(@"..\B\B.csproj", new Dictionary<string, string>())],
            },
        };
        var withoutAssets = Build(Evaluation(new(), []) with { Items = evaluation.Items });

        Assert.Equal(["src/A/A.csproj"], Build(evaluation).ProjectReferences);
        Assert.Equal(["src/A/A.csproj", "src/B/B.csproj"], withoutAssets.ProjectReferences);
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
