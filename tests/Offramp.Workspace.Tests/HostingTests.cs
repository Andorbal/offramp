using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Model;
using Offramp.Workspace.Planning;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

/// <summary>What counts as a hosted project (docs/decisions/0055-hosted-projects-belong-to-their-host.md).</summary>
public sealed class HostingTests : IDisposable
{
    private readonly ScratchDirectory _root = new("hosting");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_project_building_into_a_web_project_folder_is_hosted_by_the_deepest_one()
    {
        var projects = Hosting.Detect(
            [
                Project("src/Site/Site.csproj", kind: ProjectKind.Web) with { OutputPath = "src/Site/bin" },
                Project("src/Site/Admin/Admin.csproj", kind: ProjectKind.Web) with { OutputPath = "src/Site/bin" },
                Project("src/Site/Admin/Widgets/Widgets.csproj", kind: ProjectKind.Library) with { OutputPath = "src/Site/Admin/bin" },
                Project("src/Plugins/Tax/Tax.csproj", kind: ProjectKind.Web, references: ["src/Site/Site.csproj"]) with { OutputPath = "src/Site/Plugins/Tax" },
                Project("src/Lib/Lib.csproj") with { OutputPath = "src/Lib/bin/Debug" },
                Project("src/Tool/Tool.csproj", kind: ProjectKind.Console) with { OutputPath = "src/Site/bin" },
            ],
            _root.Path);

        Assert.Equal(
            [null, "src/Site/Site.csproj", "src/Site/Admin/Admin.csproj", "src/Site/Site.csproj", null, null],
            projects.Select(p => p.HostedBy?.Project));
        Assert.Equal(
            ["builds into src/Site/Plugins/Tax, inside src/Site", "has no Global.asax", "references src/Site/Site.csproj", "no project references it"],
            projects[3].HostedBy!.Evidence);
    }

    [Fact]
    public void A_module_whose_build_copies_its_assembly_into_the_site_is_hosted()
    {
        // DotNetNuke: modules build into their own bin; Module.build's AfterBuild copies the assembly into Website/bin.
        var projects = Hosting.Detect(
            [
                Project("Website/Website.csproj", kind: ProjectKind.Web) with { OutputPath = "Website/bin" },
                Project("Modules/Html/Html.csproj", kind: ProjectKind.Web) with { OutputPath = "Modules/Html/bin" },
                Project("Modules/Own/Own.csproj", kind: ProjectKind.Web) with { OutputPath = "Modules/Own/bin" },
            ],
            _root.Path,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Modules/Html/Html.csproj"] = ["Modules/Html/bin", "Website/bin"],
                ["Modules/Own/Own.csproj"] = ["Modules/Own/bin"],
            });

        Assert.Equal([null, "Website/Website.csproj", null], projects.Select(p => p.HostedBy?.Project));
        Assert.Equal("its build copies its assembly into Website/bin, inside Website", projects[1].HostedBy!.Evidence[0]);
    }

    [Fact]
    public void A_project_with_its_own_global_asax_or_output_folder_is_not_hosted()
    {
        _root.Write("src/Blog/Global.asax", "<%@ Application Inherits=\"Blog.Global\" %>\n");

        var projects = Hosting.Detect(
            [
                Project("src/Site/Site.csproj", kind: ProjectKind.Web) with { OutputPath = "src/Site/bin" },
                Project("src/Blog/Blog.csproj", kind: ProjectKind.Web) with { OutputPath = "src/Site/blog/bin" },
                Project("src/Site/Api/Api.csproj", kind: ProjectKind.Web) with { OutputPath = "src/Site/Api/bin" },
                Project("src/Old/Old.csproj", kind: ProjectKind.Web),
            ],
            _root.Path);

        Assert.All(projects, p => Assert.Null(p.HostedBy));
    }

    [Fact]
    public void Plan_for_a_host_lists_what_it_hosts_but_a_reference_to_the_host_does_not()
    {
        // Tax references the site; the site hosts Tax and Shipping. plan --for Tax needs the site, not Shipping.
        var model = MigrationPlannerTests.ModelOf(
            Project("src/Core/Core.csproj"),
            Project("src/Site/Site.csproj", kind: ProjectKind.Web, references: ["src/Core/Core.csproj"]),
            Project("src/Plugins/Tax/Tax.csproj", kind: ProjectKind.Web, references: ["src/Site/Site.csproj"]) with { HostedBy = new ProjectHost { Project = "src/Site/Site.csproj" } },
            Project("src/Plugins/Shipping/Shipping.csproj", kind: ProjectKind.Web, references: ["src/Plugins/Geo/Geo.csproj"]) with { HostedBy = new ProjectHost { Project = "src/Site/Site.csproj" } },
            Project("src/Plugins/Geo/Geo.csproj"));

        var site = MigrationPlanner.Plan(model, "src/Site/Site.csproj", false, []);
        var tax = MigrationPlanner.Plan(model, "src/Plugins/Tax/Tax.csproj", false, []);

        Assert.Equal(
            ["src/Core/Core.csproj", "src/Plugins/Geo/Geo.csproj", "src/Plugins/Shipping/Shipping.csproj", "src/Plugins/Tax/Tax.csproj", "src/Site/Site.csproj"],
            site.Order.Select(e => e.Project).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["src/Core/Core.csproj", "src/Plugins/Tax/Tax.csproj", "src/Site/Site.csproj"],
            tax.Order.Select(e => e.Project).Order(StringComparer.Ordinal));
        Assert.Equal(["src/Plugins/Shipping/Shipping.csproj", "src/Plugins/Tax/Tax.csproj"], Hosting.HostedProjects(model, "src/Site/Site.csproj"));
    }
}
