using Offramp.Fixtures;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

public sealed class WindowsOnlyBuildStepsTests
{
    [Fact]
    public void A_plain_project_has_no_windows_only_steps()
    {
        var evaluation = Evaluation(
            properties: new() { ["GenerateSerializationAssemblies"] = "Auto", ["PostBuildEvent"] = "echo built" });

        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [evaluation]));
    }

    [Fact]
    public void Sgen_auto_counts_only_when_the_target_ran()
    {
        var ran = Evaluation(properties: new() { ["GenerateSerializationAssemblies"] = "Auto" }, targets: ["GenerateSerializationAssemblies"]);

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [ran]));
        Assert.Equal("sgen", step.Id);
        Assert.Equal("OFR0110", step.Descriptor.Code);
    }

    [Fact]
    public void Steps_come_from_imports_and_are_reported_in_order_once()
    {
        var net48 = Evaluation(
            properties: new() { ["PreBuildEvent"] = "xcopy /y a b\r\necho second line" },
            items: new() { ["COMFileReference"] = [new EvaluatedItem("typelib.tlb", new Dictionary<string, string>())] },
            imports: ["C:/VS/MSBuild/Microsoft/VisualStudio/v17.0/TextTemplating/Microsoft.TextTemplating.targets", "C:/VS/SSDT/Microsoft.Data.Tools.Schema.SqlTasks.targets"]);
        var again = net48 with { };

        var steps = WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [net48, again]);

        Assert.Equal(["com", "t4", "ssdt", "build-event"], steps.Select(s => s.Id));
        Assert.Equal("imports Microsoft.TextTemplating.targets", steps[1].Evidence);
        Assert.Equal("PreBuildEvent: xcopy /y a b", steps[3].Evidence);
    }

    [Fact]
    public void The_web_targets_count_when_Visual_Studio_provides_them_and_not_when_the_package_does()
    {
        var visualStudio = Evaluation(imports: ["C:/VS/MSBuild/Microsoft/VisualStudio/v17.0/WebApplications/Microsoft.WebApplication.targets"]);
        var package = Evaluation(imports: ["/home/me/.nuget/packages/msbuild.microsoft.visualstudio.web.targets/14.0.0.3/build/../tools/VSToolsPath/WebApplications/Microsoft.WebApplication.targets"]);

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [visualStudio]));
        Assert.Equal("web-targets", step.Id);
        Assert.Equal("OFR0116", step.Descriptor.Code);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [package]));
    }

    [Fact]
    public void Precompiled_views_need_the_aspnet_compiler()
    {
        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [Evaluation(properties: new() { ["MvcBuildViews"] = "true" })]));

        Assert.Equal("aspnet-compiler", step.Id);
        Assert.Equal("OFR0116", step.Descriptor.Code);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [Evaluation(properties: new() { ["MvcBuildViews"] = "false" })]));
    }

    [Fact]
    public void A_missing_web_targets_import_is_the_step_even_without_an_evaluation()
    {
        const string message = "The imported project \"/usr/local/share/dotnet/sdk/10.0.100/Microsoft/VisualStudio/v17.0/WebApplications/Microsoft.WebApplication.targets\" was not found.";
        BuildError[] errors =
        [
            new("MSB4019", "The imported project \"/x/Other.targets\" was not found.", "/repo/src/A/A.csproj", null, null, null),
            new("MSB4019", message, "/repo/src/A/A.csproj", null, null, null),
        ];

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [], errors));

        Assert.Equal("web-targets", step.Id);
        Assert.Null(WindowsOnlyBuildSteps.FromEvaluationError("MSB4019", "The imported project \"/x/Other.targets\" was not found."));
        Assert.Null(WindowsOnlyBuildSteps.FromEvaluationError("CS0246", message));
    }

    [Fact]
    [ProducesDiagnostic("OFR0117")]
    public void A_path_that_exists_only_in_another_letter_case_is_named_with_its_spelling_on_disk()
    {
        using var repo = new ScratchDirectory("path-case");
        repo.Write("build/Scripts/Package.targets", "<Project />");
        Assert.SkipWhen(Directory.Exists(Path.Combine(repo.Path, "BUILD")), "This file system ignores letter case (Windows, macOS), so every spelling exists.");
        repo.Write("src/A/Layout/XMLLayout.cs", "class L {}");
        var import = Path.Combine(repo.Path, "Build", "Scripts", "Package.Targets");
        var source = Path.Combine(repo.Path, "src", "A", "Layout", "XmlLayout.cs");
        BuildError[] errors =
        [
            new("MSB4019", $"The imported project \"{import}\" was not found. Confirm that the expression in the Import declaration \"x\" is correct.", "/repo/src/A/A.csproj", null, null, null),
            new("CS2001", $"Source file '{source}' could not be found.", "/repo/src/A/A.csproj", null, null, null),
            new("CS2001", $"Source file '{Path.Combine(repo.Path, "src", "A", "Gone.cs")}' could not be found.", "/repo/src/A/A.csproj", null, null, null),
        ];

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [], errors));

        Assert.Equal("path-case", step.Id);
        Assert.Equal("OFR0117", step.Descriptor.Code);
        Assert.Equal($"{import}: 'Build' is 'build' on disk (and 1 more)", step.Evidence);
    }

    [Fact]
    public void A_path_missing_in_every_letter_case_is_not_a_case_mismatch()
    {
        using var repo = new ScratchDirectory("path-case");
        BuildError[] errors = [new("CS2001", $"Source file '{Path.Combine(repo.Path, "Gone.cs")}' could not be found.", "/repo/src/A/A.csproj", null, null, null)];

        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [], errors));
    }

    [Fact]
    [ProducesDiagnostic("OFR0118")]
    [ProducesDiagnostic("OFR0119")]
    public void Inline_tasks_non_string_resources_and_cmd_commands_are_named_from_the_build_errors()
    {
        BuildError[] errors =
        [
            new("MSB4801", "The task factory \"CodeTaskFactory\" is not supported on the .NET Core version of MSBuild.", "/repo/src/Web/Web.csproj", "/repo/packages/Microsoft.CodeDom.Providers.DotNetCompilerPlatform.2.0.1/build/net46/Microsoft.CodeDom.Providers.DotNetCompilerPlatform.props", 31, 5),
            new("MSB3823", "Non-string resources require the property GenerateResourceUsePreserializedResources to be set to true.", "/repo/src/Web/Web.csproj", "/usr/share/dotnet/sdk/10.0.100/Microsoft.Common.CurrentVersion.targets", 3442, 5),
            new("MSB3073", "The command \"echo done\" exited with code 1.", "/repo/src/Web/Web.csproj", null, null, null),
            new("MSB3073", "The command \"XCOPY \"bin\\Debug\\Web*\" \"../Website/bin\" /S /Y\" exited with code 127.", "/repo/src/Web/Web.csproj", null, null, null),
        ];

        var steps = WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [], errors);

        Assert.Equal(["build-event", "inline-task", "resources"], steps.Select(s => s.Id));
        Assert.Equal("Exec: XCOPY \"bin\\Debug\\Web*\" \"../Website/bin\" /S /Y (MSB3073)", steps[0].Evidence);
        Assert.Equal("CodeTaskFactory in Microsoft.CodeDom.Providers.DotNetCompilerPlatform.props (MSB4801)", steps[1].Evidence);
        Assert.Equal("OFR0118", steps[1].Descriptor.Code);
        Assert.Equal("non-string resources in a .resx file (MSB3823)", steps[2].Evidence);
        Assert.Equal("OFR0119", steps[2].Descriptor.Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR0117")]
    public void The_project_files_mismatches_come_first_and_join_those_only_the_errors_show()
    {
        using var repo = new ScratchDirectory("path-case");
        repo.Write("intl/markets/master.xml", "<markets />");
        Assert.SkipWhen(File.Exists(repo.Combine("intl", "markets", "Master.xml")), "This file system ignores letter case.");
        var copied = Path.Combine(repo.Path, "src", "Core", "..", "..", "intl", "markets", "Master.xml");
        var files = new ProjectFileFindings
        {
            CaseMismatches =
            [
                new CaseMismatch("/repo/src/.nuget/nuget.targets", "/repo/src/.nuget/NuGet.targets", "/repo/src/.nuget/nuget.targets: 'nuget.targets' is 'NuGet.targets' on disk"),
                new CaseMismatch("/repo/src/A/Multimap.cs", "/repo/src/A/MultiMap.cs", "/repo/src/A/Multimap.cs: 'Multimap.cs' is 'MultiMap.cs' on disk"),
            ],
        };
        BuildError[] errors =
        [
            new("MSB3030", $"Could not copy the file \"{copied}\" because it was not found.", "/repo/src/Core/Core.csproj", null, null, null),
        ];

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/Core/Core.csproj", [], errors, new BuildStepContext { Files = files }));

        Assert.Equal("/repo/src/.nuget/nuget.targets: 'nuget.targets' is 'NuGet.targets' on disk (and 2 more)", step.Evidence);
        Assert.Equal(["/repo/src/.nuget/nuget.targets", "/repo/src/A/Multimap.cs", repo.Path + "/intl/markets/Master.xml"], step.Paths);
    }

    [Fact]
    [ProducesDiagnostic("OFR0119")]
    public void Non_string_resources_are_named_by_file_before_any_build_unless_every_target_embeds_them_preserialized()
    {
        var files = new ProjectFileFindings
        {
            NonStringResources = [new NonStringResourceFile("/repo/src/A/Images.resx", 58), new NonStringResourceFile("/repo/src/A/Main.resx", 2)],
        };
        var context = new BuildStepContext { Files = files };
        var preserialized = Evaluation(properties: new() { ["GenerateResourceUsePreserializedResources"] = "true" });

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [Evaluation()], [], context));

        Assert.Equal("resources", step.Id);
        Assert.Equal("58 non-string resource(s) in /repo/src/A/Images.resx (and 1 more .resx file(s))", step.Evidence);
        Assert.Equal(["/repo/src/A/Images.resx", "/repo/src/A/Main.resx"], step.Paths);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [preserialized], [], context));
        Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [preserialized, Evaluation() with { TargetFramework = "net461" }], [], context));
    }

    [Fact]
    [ProducesDiagnostic("OFR0124")]
    public void Microsoft_bcl_build_is_a_step_until_its_redirects_are_skipped()
    {
        // SmartStoreNET's FacebookAuth and Open Live Writer's PostEditor: MSB4062 from EnsureBindingRedirects.
        string[] imports = ["/repo/src/packages/Microsoft.Bcl.Build.1.0.21/build/Microsoft.Bcl.Build.targets"];
        const string message = "The \"EnsureBindingRedirects\" task could not be loaded from the assembly /repo/src/packages/Microsoft.Bcl.Build.1.0.21/build/Microsoft.Bcl.Build.Tasks.dll. Could not load file or assembly 'Microsoft.Build.Utilities.v4.0'.";

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [Evaluation(imports: imports, properties: new() { ["SkipEnsureBindingRedirects"] = "false" })]));
        var fromError = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [], [new BuildError("MSB4062", message, "/repo/src/A/A.csproj", null, null, null)]));

        Assert.Equal("bcl-build", step.Id);
        Assert.Equal("OFR0124", step.Descriptor.Code);
        Assert.Equal("imports Microsoft.Bcl.Build.targets, whose EnsureBindingRedirects task needs .NET Framework's MSBuild", step.Evidence);
        Assert.Equal("bcl-build", fromError.Id);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [Evaluation(imports: imports, properties: new() { ["SkipEnsureBindingRedirects"] = "true" })]));
        Assert.True(WindowsOnlyBuildSteps.IsStepCode("OFR0124"));
    }

    [Fact]
    [ProducesDiagnostic("OFR0125")]
    public void An_mstest_v1_reference_without_a_hint_path_needs_visual_studio()
    {
        // Open Live Writer: 363 CS0246/CS0234 errors in two test projects.
        var visualStudio = new EvaluatedItem("Microsoft.VisualStudio.QualityTools.UnitTestFramework, Version=10.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL", new Dictionary<string, string>());
        var checkedIn = new EvaluatedItem("Microsoft.VisualStudio.QualityTools.UnitTestFramework", new Dictionary<string, string> { ["HintPath"] = @"..\lib\Microsoft.VisualStudio.QualityTools.UnitTestFramework.dll" });

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/Tests/Tests.csproj", [Evaluation(items: new() { ["Reference"] = [visualStudio] })]));

        Assert.Equal("mstest-v1", step.Id);
        Assert.Equal("OFR0125", step.Descriptor.Code);
        Assert.Equal("Reference Microsoft.VisualStudio.QualityTools.UnitTestFramework (MSTest v1), which only Visual Studio installs", step.Evidence);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/Tests/Tests.csproj", [Evaluation(items: new() { ["Reference"] = [checkedIn] })]));
    }

    private static EvaluatedProject Evaluation(
        Dictionary<string, string>? properties = null,
        Dictionary<string, IReadOnlyList<EvaluatedItem>>? items = null,
        string[]? imports = null,
        string[]? targets = null) => new()
        {
            ProjectFile = "/repo/src/A/A.csproj",
            TargetFramework = "net48",
            Properties = properties ?? [],
            Items = items ?? [],
            Imports = imports ?? [],
            TargetsExecuted = new HashSet<string>(targets ?? [], StringComparer.OrdinalIgnoreCase),
        };
}
