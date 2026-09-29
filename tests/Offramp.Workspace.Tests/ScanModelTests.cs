using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;
using Offramp.Workspace.Scanning;

namespace Offramp.Workspace.Tests;

/// <summary>What the workspace model records, and that the same inputs give the same model.</summary>
public sealed class ScanModelTests
{
    /// <summary>
    /// SmartStoreNET P1 #8: the compiler log lists calls in the order a parallel build finished them, so a call's
    /// position differed between two scans; the model names calls by project and target framework instead.
    /// </summary>
    [Fact]
    public void Compiler_calls_are_named_the_same_whatever_order_the_log_lists_them_in()
    {
        var mapper = CapturePathMapper.Local("/repo");
        CompilerCallInfo[] calls =
        [
            new(0, "/repo/src/A/A.csproj", "net48", IsCSharp: true),
            new(1, "/repo/src/A/A.csproj", "net10.0", IsCSharp: true),
            new(2, "/repo/src/Legacy/Legacy.csproj", null, IsCSharp: true),
        ];
        CompilerCallInfo[] reordered = [calls[2] with { Index = 0 }, calls[0] with { Index = 1 }, calls[1] with { Index = 2 }];

        var first = ScanRunner.MapCalls(calls, mapper, ".offramp/build.complog");
        var second = ScanRunner.MapCalls(reordered, mapper, ".offramp/build.complog");

        Assert.Equal(first.OrderBy(e => e.Key), second.OrderBy(e => e.Key));
        Assert.Equal(new Core.Model.CompilerCallRef(".offramp/build.complog", "src/Legacy/Legacy.csproj", null), first[("src/Legacy/Legacy.csproj", "")]);
    }

    /// <summary>
    /// SmartStoreNET P1 #8, NHibernate P2: two full scans of the same tree wrote different models (the built log's
    /// hash, and compiler-call positions); now they are byte-identical.
    /// </summary>
    [Fact]
    public async Task Two_full_scans_of_the_same_tree_write_the_same_model()
    {
        var scanned = await ScannedFixtures.ScanAsync("netfx-only");
        using var _ = scanned.Repository;
        var first = scanned.ModelJson;

        var again = await ScanRunner.RunAsync(ScannedFixtures.Request(scanned.Root, new DiagnosticBag()), CancellationToken.None);

        Assert.Equal(ScanFailure.None, again.Failure);
        Assert.Null(again.Model!.Source.Sha256);
        Assert.Equal(first, scanned.ModelJson);
    }

    /// <summary>Open Live Writer P1 #9: a settings file the projects import is an input of the model, and so is NuGet.config.</summary>
    [Fact]
    public async Task Files_the_projects_import_are_inputs_of_the_model()
    {
        using var repo = new ScratchDirectory("scan-imports");
        repo.Write("Directory.Build.props", "<Project />");
        repo.Write("Directory.Build.targets", "<Project />");
        repo.Write("NuGet.config", "<configuration><packageSources><clear /></packageSources></configuration>");
        repo.Write("build/common.props", "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");
        repo.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\build\common.props" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        repo.Write("App/Code.cs", "namespace App; public static class Code { }\n");
        repo.Write("App.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");

        var outcome = await ScanRunner.RunAsync(ScannedFixtures.Request(repo.Path, new DiagnosticBag()), CancellationToken.None);

        Assert.Equal(ScanFailure.None, outcome.Failure);
        var inputs = outcome.Model!.Inputs.Select(i => i.Path).ToList();
        Assert.Contains("build/common.props", inputs);
        Assert.Contains("NuGet.config", inputs);
        Assert.DoesNotContain(inputs, i => i.Contains("/obj/", StringComparison.Ordinal));

        repo.Write("build/common.props", "<Project><PropertyGroup><LangVersion>12</LangVersion></PropertyGroup></Project>");
        var staleness = Store.WorkspaceInputs.Compare(outcome.Model, repo.Path, System.IO.Path.Combine(repo.Path, ".offramp"));
        Assert.Equal(["build/common.props"], staleness.Changed);
    }

    /// <summary>
    /// NHibernate P1 #14, Open Live Writer P2: a project whose compiler call logged errors has a call in the compiler
    /// log, and was not partial although its compilation is broken.
    /// </summary>
    [Fact]
    public async Task A_project_whose_compilation_failed_is_partial()
    {
        using var repo = new ScratchDirectory("scan-failed-compile");
        repo.Write("Directory.Build.props", "<Project />");
        repo.Write("Directory.Build.targets", "<Project />");
        foreach (var name in new[] { "Good", "Broken" })
        {
            repo.Write($"{name}/{name}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
        }

        repo.Write("Good/Code.cs", "namespace Good; public static class Code { }\n");
        repo.Write("Broken/Code.cs", "namespace Broken; public static class Code { public static int M() => Missing.Value; }\n");
        repo.Write("App.slnx", "<Solution>\n  <Project Path=\"Broken/Broken.csproj\" />\n  <Project Path=\"Good/Good.csproj\" />\n</Solution>\n");
        var bag = new DiagnosticBag();

        var outcome = await ScanRunner.RunAsync(ScannedFixtures.Request(repo.Path, bag), CancellationToken.None);

        Assert.Equal(ScanFailure.None, outcome.Failure);
        Assert.False(outcome.Result!.BuildSucceeded);
        var broken = outcome.Model!.Projects.Single(p => p.Name == "Broken");
        Assert.NotEmpty(broken.CompilerCalls);
        Assert.True(broken.Partial);
        Assert.False(outcome.Model.Projects.Single(p => p.Name == "Good").Partial);
        Assert.Equal(["Broken/Broken.csproj"], outcome.Result.Partial);
    }

    /// <summary>
    /// NHibernate P2: a legacy project's <c>DefineConstants</c> of <c>NET,NET_2_0;DEBUG</c> was recorded with
    /// <c>NET,NET_2_0</c> as one symbol; the compiler splits on commas too, and its own call is what the model records.
    /// </summary>
    [Fact]
    public void Define_constants_are_the_symbols_the_compiler_sees()
    {
        using var repo = new ScratchDirectory("defines");
        repo.Write("src/Lib/Lib.csproj", "<Project />");
        var evaluation = new EvaluatedProject
        {
            ProjectFile = repo.Combine("src", "Lib", "Lib.csproj"),
            TargetFramework = "net40",
            Properties = new Dictionary<string, string> { ["DefineConstants"] = "NET,NET_2_0;DEBUG; TRACE" },
            Items = new Dictionary<string, IReadOnlyList<EvaluatedItem>>(),
        };
        ProjectBuildContext Context(Dictionary<(string, string), IReadOnlyList<string>> defines) => new()
        {
            Paths = CapturePathMapper.Local(repo.Path),
            Config = new Core.Configuration.OfframpConfig(),
            CompilerCalls = new Dictionary<(string, string), Core.Model.CompilerCallRef>(),
            CompilerDefines = defines,
            Excluded = new PathGlobs([]),
        };

        var evaluated = ProjectModelBuilder.Build("src/Lib/Lib.csproj", [evaluation], Context([]));
        var compiled = ProjectModelBuilder.Build("src/Lib/Lib.csproj", [evaluation], Context(new() { [("src/Lib/Lib.csproj", "")] = ["NET", "NET_2_0", "DEBUG", "TRACE", "LEGACY"] }));

        Assert.Equal(["NET", "NET_2_0", "DEBUG", "TRACE"], evaluated.DefineConstants["net40"]);
        Assert.Equal(["NET", "NET_2_0", "DEBUG", "TRACE", "LEGACY"], compiled.DefineConstants["net40"]);
        Assert.Equal(["DEBUG", "TRACE", "_MyType"], ProjectModelBuilder.DefinedSymbols("DEBUG=-1,TRACE=-1,_MyType=\"Windows\""));
    }
}
