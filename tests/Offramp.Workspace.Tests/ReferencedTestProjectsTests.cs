using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

/// <summary>Test projects that applications use (ADR 0074; issue #52).</summary>
public sealed class ReferencedTestProjectsTests
{
    private static readonly ProjectFacts NUnitLibrary = new() { PackageIds = new HashSet<string>(["NUnit"], StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void A_project_an_application_references_is_what_its_other_evidence_says()
    {
        var (projects, kept) = Apply(
            Project("src/Core/Core.csproj", ProjectKind.Test, "PackageReference NUnit"),
            Project("src/Web/Web.csproj", ProjectKind.Web, "Sdk=Microsoft.NET.Sdk.Web", "src/Core/Core.csproj"));

        var core = projects.Single(p => p.Id == "src/Core/Core.csproj");
        Assert.Equal(ProjectKind.Library, core.Kind);
        Assert.Equal("OutputType=Library (PackageReference NUnit ignored: the application src/Web/Web.csproj uses it)", core.KindEvidence);
        Assert.Empty(kept);
    }

    [Fact]
    public void What_an_application_uses_through_other_projects_is_production_code_too()
    {
        // Web -> Services -> Core, both carrying NUnit: the application ships both.
        var (projects, _) = Apply(
            Project("src/Core/Core.csproj", ProjectKind.Test, "PackageReference NUnit"),
            Project("src/Services/Services.csproj", ProjectKind.Test, "PackageReference NUnit", "src/Core/Core.csproj"),
            Project("src/Web/Web.csproj", ProjectKind.Web, "Sdk=Microsoft.NET.Sdk.Web", "src/Services/Services.csproj"));

        Assert.All(projects.Where(p => p.Id != "src/Web/Web.csproj"), p => Assert.Equal(ProjectKind.Library, p.Kind));
        Assert.EndsWith("the application src/Web/Web.csproj uses it)", projects.Single(p => p.Id == "src/Core/Core.csproj").KindEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_projects_only_tests_libraries_or_a_runner_reference_stay_test_projects()
    {
        // A shared test helper (Testing), a test project a console runner references, and a library that gets
        // NUnit through the kit it references (dead-code-evidence's Specs), which nothing marks a test project.
        var (projects, kept) = Apply(
            Project("tests/Testing/Testing.csproj", ProjectKind.Test, "PackageReference NUnit"),
            Project("tests/Core.Tests/Core.Tests.csproj", ProjectKind.Test, "PackageReference NUnit", "tests/Testing/Testing.csproj"),
            Project("tests/Runner/Runner.csproj", ProjectKind.Console, "OutputType=Exe", "tests/Core.Tests/Core.Tests.csproj"),
            Project("src/TestKit/TestKit.csproj", ProjectKind.Test, "PackageReference NUnit"),
            Project("src/Specs/Specs.csproj", ProjectKind.Library, "OutputType=Library", "src/TestKit/TestKit.csproj"));

        Assert.All(projects.Where(p => p.Kind != ProjectKind.Console && p.Id != "src/Specs/Specs.csproj"), p => Assert.Equal(ProjectKind.Test, p.Kind));
        Assert.Empty(kept);
    }

    [Fact]
    public void Test_sdk_property_demotes_when_a_deployed_application_uses_the_project()
    {
        var projects = new[]
        {
            Project("src/Helpers/Helpers.csproj", ProjectKind.Test, "IsTestProject=true", "src/Core/Core.csproj"),
            Project("src/Core/Core.csproj", ProjectKind.Library, "OutputType=Library"),
            Project("src/Web/Web.csproj", ProjectKind.Web, "Sdk=Microsoft.NET.Sdk.Web", "src/Helpers/Helpers.csproj"),
        };
        var facts = projects.ToDictionary(p => p.Id, p => p.Id == "src/Helpers/Helpers.csproj"
            ? new ProjectFacts { IsTestProject = true, PackageIds = new HashSet<string>(["Microsoft.NET.Test.Sdk"], StringComparer.OrdinalIgnoreCase) }
            : new ProjectFacts(), StringComparer.Ordinal);

        var (classified, kept) = ReferencedTestProjects.Apply(projects, facts);
        var helper = classified.Single(p => p.Id == "src/Helpers/Helpers.csproj");

        Assert.Equal(ProjectKind.Library, helper.Kind);
        Assert.False(helper.IsTestProject);
        Assert.True(helper.HostsTests);
        Assert.Empty(kept);
    }

    [Fact]
    public void Is_test_project_only_used_by_a_console_runner_stays_test()
    {
        var projects = new[]
        {
            Project("tests/Helpers/Helpers.csproj", ProjectKind.Test, "IsTestProject=true"),
            Project("tests/Runner/Runner.csproj", ProjectKind.Console, "OutputType=Exe", "tests/Helpers/Helpers.csproj"),
        };
        var facts = new Dictionary<string, ProjectFacts>(StringComparer.Ordinal)
        {
            ["tests/Helpers/Helpers.csproj"] = new() { IsTestProject = true },
            ["tests/Runner/Runner.csproj"] = new(),
        };

        var (classified, kept) = ReferencedTestProjects.Apply(projects, facts);

        Assert.Equal(ProjectKind.Test, classified.Single(p => p.Id == "tests/Helpers/Helpers.csproj").Kind);
        Assert.Empty(kept);
    }

    [Fact]
    public void Test_guid_stays_test_when_a_deployed_application_uses_the_project()
    {
        var (projects, kept) = ReferencedTestProjects.Apply(
            [Project("src/Helpers/Helpers.csproj", ProjectKind.Test, "ProjectTypeGuids {3AC096D0-A1C2-E12C-1390-A8335801FDAB}"),
             Project("src/App/App.csproj", ProjectKind.Web, "Sdk=Microsoft.NET.Sdk.Web", "src/Helpers/Helpers.csproj")],
            new Dictionary<string, ProjectFacts>(StringComparer.Ordinal)
            {
                ["src/Helpers/Helpers.csproj"] = new() { ProjectTypeGuids = "{3AC096D0-A1C2-E12C-1390-A8335801FDAB}" },
                ["src/App/App.csproj"] = new(),
            });

        Assert.Equal(ProjectKind.Test, projects.Single(p => p.Id == "src/Helpers/Helpers.csproj").Kind);
        Assert.Single(kept);
    }

    [Fact]
    public void Winexe_test_framework_with_service_evidence_is_service_and_hosts_tests()
    {
        var facts = new ProjectFacts { OutputType = "WinExe", PackageIds = new HashSet<string>(["NUnit"], StringComparer.OrdinalIgnoreCase), Sdk = "Microsoft.NET.Sdk.Worker" };
        Assert.Equal((ProjectKind.Service, "Sdk=Microsoft.NET.Sdk.Worker"), ProjectKindDetector.Detect(facts));
        Assert.NotNull(ProjectKindDetector.TestFramework(facts));
    }

    [Fact]
    public void An_explicit_test_project_or_an_override_is_kept()
    {
        var (projects, kept) = Apply(
            Project("src/Helpers/Helpers.csproj", ProjectKind.Test, "ProjectTypeGuids {3AC096D0-A1C2-E12C-1390-A8335801FDAB}"),
            Project("src/Mine/Mine.csproj", ProjectKind.Test, "offramp.yml") with { Config = new ProjectConfigState { KindOverride = ProjectKind.Test } },
            Project("src/App/App.csproj", ProjectKind.Service, "Reference System.ServiceProcess + OutputType=Exe", "src/Helpers/Helpers.csproj", "src/Mine/Mine.csproj"));

        Assert.All(projects.Where(p => p.Id != "src/App/App.csproj"), p => Assert.Equal(ProjectKind.Test, p.Kind));
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, p => p.Project.Id == "src/Helpers/Helpers.csproj" && p.Referencer == "src/App/App.csproj");
        Assert.Contains(kept, p => p.Project.Id == "src/Mine/Mine.csproj" && p.Referencer == "src/App/App.csproj");
    }

    private static (IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<(ProjectInfo Project, string Referencer)> Kept) Apply(params ProjectInfo[] projects) =>
        ReferencedTestProjects.Apply(projects, projects.ToDictionary(p => p.Id, p => p.KindEvidence == "IsTestProject=true" ? NUnitLibrary with { IsTestProject = true } : NUnitLibrary, StringComparer.Ordinal));

    private static ProjectInfo Project(string id, ProjectKind kind, string evidence, params string[] references) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(id),
        Kind = kind,
        KindEvidence = evidence,
        IsTestProject = evidence == "IsTestProject=true",
        ProjectReferences = references,
    };
}
