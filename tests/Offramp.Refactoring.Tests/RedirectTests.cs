using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Refactoring.Dependencies.Redirects;

namespace Offramp.Refactoring.Tests;

/// <summary>ROADMAP M6 acceptance: the web project's redirects are regenerated from the graph and stale ones pruned.</summary>
public sealed class RedirectTests
{
    private const string WebConfig = "src/Customer.Api/web.config";

    [Fact]
    [ProducesDiagnostic("OFR1504")]
    public async Task Needed_redirects_stay_and_stale_ones_are_reported()
    {
        var fixture = await ScannedFixtures.GetAsync("versions");
        var diagnostics = new DiagnosticBag();

        var plan = RedirectPlanner.Plan(Request(fixture.Root, fixture.Outcome.Model!, diagnostics, prune: false));

        var app = Assert.Single(plan.Result.Apps);
        Assert.Equal(("src/Customer.Api/Customer.Api.csproj", WebConfig), (app.Project, app.ConfigFile!));
        Assert.Equal(
            [
                ("Newtonsoft.Json", RedirectAction.Unchanged, "0.0.0.0-9.0.0.0", "9.0.0.0"),
                ("System.Net.Http.Formatting", RedirectAction.Unchanged, "0.0.0.0-5.2.3.0", "5.2.3.0"),
                ("System.Web.Mvc", RedirectAction.Stale, "0.0.0.0-5.2.7.0", "5.2.7.0"),
            ],
            app.Redirects.Select(r => (r.Assembly, r.Action, r.OldVersion!, r.NewVersion!)));
        Assert.Equal(["6.0.0.0", "9.0.0.0"], app.Redirects[0].Referenced);
        Assert.Null(plan.ChangeSet);
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR1504" && d.File == WebConfig);
    }

    [Fact]
    [ProducesDiagnostic("OFR1503")]
    public async Task Prune_removes_only_the_stale_entry_and_keeps_every_other_byte()
    {
        var fixture = await ScannedFixtures.GetAsync("versions");
        var original = File.ReadAllText(Path.Combine(fixture.Root, "src", "Customer.Api", "web.config"));
        var diagnostics = new DiagnosticBag();

        var plan = RedirectPlanner.Plan(Request(fixture.Root, fixture.Outcome.Model!, diagnostics, prune: true));

        var after = System.Text.Encoding.UTF8.GetString(Assert.Single(plan.ChangeSet!.Edits).After);
        var block = original[original.IndexOf("      <dependentAssembly>\n        <assemblyIdentity name=\"System.Web.Mvc\"", StringComparison.Ordinal)..];
        block = block[..(block.IndexOf("</dependentAssembly>\n", StringComparison.Ordinal) + "</dependentAssembly>\n".Length)];
        Assert.Equal(original.Replace(block, "", StringComparison.Ordinal), after);
        Assert.Equal(1, plan.Result.Summary.Pruned);
        Assert.True(diagnostics.Contains("OFR1503"));
    }

    [Fact]
    [ProducesDiagnostic("OFR1501")]
    [ProducesDiagnostic("OFR1502")]
    public async Task A_missing_redirect_is_added_and_a_wrong_one_is_changed()
    {
        var fixture = await ScannedFixtures.GetAsync("versions");
        using var copy = await FixtureRepository.CreateAsync("versions", git: false);
        var path = Path.Combine(copy.Path, "src", "Customer.Api", "web.config");
        var original = File.ReadAllText(path);
        var newtonsoft = original[original.IndexOf("      <dependentAssembly>\n        <assemblyIdentity name=\"Newtonsoft.Json\"", StringComparison.Ordinal)..];
        newtonsoft = newtonsoft[..(newtonsoft.IndexOf("</dependentAssembly>\n", StringComparison.Ordinal) + "</dependentAssembly>\n".Length)];

        File.WriteAllText(path, original.Replace("newVersion=\"9.0.0.0\"", "newVersion=\"8.0.0.0\"", StringComparison.Ordinal));
        var changedDiagnostics = new DiagnosticBag();
        var changed = RedirectPlanner.Plan(Request(copy.Path, fixture.Outcome.Model!, changedDiagnostics, prune: false));
        File.WriteAllText(path, original.Replace(newtonsoft, "", StringComparison.Ordinal));
        var addedDiagnostics = new DiagnosticBag();
        var added = RedirectPlanner.Plan(Request(copy.Path, fixture.Outcome.Model!, addedDiagnostics, prune: false));

        Assert.Equal((RedirectAction.Changed, "8.0.0.0"), (changed.Result.Apps[0].Redirects[0].Action, changed.Result.Apps[0].Redirects[0].Was!));
        Assert.Equal(original, System.Text.Encoding.UTF8.GetString(changed.ChangeSet!.Edits[0].After));
        Assert.True(changedDiagnostics.Contains("OFR1502"));
        Assert.Equal(RedirectAction.Added, added.Result.Apps[0].Redirects[0].Action);
        var withAdded = System.Text.Encoding.UTF8.GetString(added.ChangeSet!.Edits[0].After);
        Assert.Contains(newtonsoft.Replace("culture=\"neutral\" />", "culture=\"neutral\" />", StringComparison.Ordinal), withAdded, StringComparison.Ordinal);
        Assert.EndsWith("    </assemblyBinding>\n  </runtime>\n</configuration>\n", withAdded, StringComparison.Ordinal);
        Assert.True(addedDiagnostics.Contains("OFR1501"));
    }

    [Fact]
    public async Task A_packages_config_application_deploys_what_it_and_its_references_install()
    {
        // Billing.Tool, a legacy console, references Billing, whose packages.config installs
        // Newtonsoft.Json 13.0.3; neither has a restored graph to read. Its App.config redirects
        // Newtonsoft.Json, which must not look stale.
        var scanned = await ScannedFixtures.ScanAsync("legacy-csproj");
        using var _ = scanned.Repository;
        var diagnostics = new DiagnosticBag();

        var plan = RedirectPlanner.Plan(Request(scanned.Root, scanned.Outcome.Model!, diagnostics, prune: true) with { Apps = ["src/Billing.Tool/Billing.Tool.csproj"] });

        var redirect = Assert.Single(Assert.Single(plan.Result.Apps).Redirects);
        Assert.Equal(("Newtonsoft.Json", RedirectAction.Unchanged), (redirect.Assembly, redirect.Action));
        Assert.False(diagnostics.Contains("OFR1503") || diagnostics.Contains("OFR1504"));

        // The same entry twice, as hand-edited configuration files sometimes have, is read, not fatal.
        var config = Path.Combine(scanned.Root, "src", "Billing.Tool", "App.config");
        var text = File.ReadAllText(config);
        var entry = text[text.IndexOf("<dependentAssembly>", StringComparison.Ordinal)..(text.IndexOf("</dependentAssembly>", StringComparison.Ordinal) + "</dependentAssembly>".Length)];
        File.WriteAllText(config, text.Replace(entry, entry + "\n      " + entry, StringComparison.Ordinal));

        var twice = RedirectPlanner.Plan(Request(scanned.Root, scanned.Outcome.Model!, new DiagnosticBag(), prune: false) with { Apps = ["src/Billing.Tool/Billing.Tool.csproj"] });

        Assert.All(Assert.Single(twice.Result.Apps).Redirects, r => Assert.Equal(RedirectAction.Unchanged, r.Action));
    }

    [Fact]
    [ProducesDiagnostic("OFR1506")]
    public async Task An_application_with_a_partial_model_is_left_alone_even_with_prune()
    {
        // As on a fresh DotNetNuke checkout on Linux: a project whose build failed has no references in the
        // model, so Newtonsoft.Json looked unused and --prune would have removed its live redirect.
        var scanned = await ScannedFixtures.GetAsync("legacy-csproj");
        var model = scanned.Outcome.Model!;
        var partial = model with { Projects = [.. model.Projects.Select(p => p.Id == "src/Billing/Billing.csproj" ? p with { Partial = true } : p)] };
        var diagnostics = new DiagnosticBag();

        var plan = RedirectPlanner.Plan(Request(scanned.Root, partial, diagnostics, prune: true) with { Apps = ["src/Billing.Tool/Billing.Tool.csproj"] });

        var app = Assert.Single(plan.Result.Apps);
        Assert.Empty(app.Redirects);
        Assert.Equal("src/Billing/Billing.csproj, which it references, is partial in the workspace model (its build failed), so what it deploys is not known", app.Skipped);
        Assert.Null(plan.ChangeSet);
        Assert.Equal("src/Billing.Tool/Billing.Tool.csproj", Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR1506").Project);
    }

    [Fact]
    [ProducesDiagnostic("OFR1505")]
    public async Task A_redirect_down_to_an_older_deployed_version_is_never_written()
    {
        // Billing's packages.config also lists a package whose assembly references Newtonsoft.Json
        // 14.0.0.0, while Newtonsoft.Json 13.0.3 (13.0.0.0) is what deploys: packages.config lets that happen.
        var scanned = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            var lib = Directory.CreateDirectory(Path.Combine(root, "packages", "Contoso.Needs.Newer.1.0.0", "lib", "net45")).FullName;
            File.WriteAllBytes(Path.Combine(lib, "Contoso.Needs.Newer.dll"), StubAssembly.Build(new RecordedAssembly
            {
                Name = "Contoso.Needs.Newer",
                Version = "1.0.0.0",
                References = [new RecordedAssemblyReference("Newtonsoft.Json", "14.0.0.0", "30ad4fe6b2a6aeed")],
            }));
            var packagesConfig = Path.Combine(root, "src", "Billing", "packages.config");
            File.WriteAllText(packagesConfig, File.ReadAllText(packagesConfig).Replace(
                "</packages>", "  <package id=\"Contoso.Needs.Newer\" version=\"1.0.0\" targetFramework=\"net48\" />\n</packages>", StringComparison.Ordinal));
            return request;
        });
        using var _ = scanned.Repository;
        var diagnostics = new DiagnosticBag();

        var plan = RedirectPlanner.Plan(Request(scanned.Root, scanned.Outcome.Model!, diagnostics, prune: false) with { Apps = ["src/Billing.Tool/Billing.Tool.csproj"] });

        var redirect = Assert.Single(Assert.Single(plan.Result.Apps).Redirects);
        Assert.Equal(("Newtonsoft.Json", RedirectAction.Unchanged, "13.0.0.0"), (redirect.Assembly, redirect.Action, redirect.NewVersion));
        var older = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR1505");
        Assert.Contains("deploys Newtonsoft.Json 13.0.0.0, older than the 14.0.0.0", older.Message, StringComparison.Ordinal);
    }

    private static RedirectsRequest Request(string root, WorkspaceModel model, DiagnosticBag diagnostics, bool prune) => new()
    {
        RepositoryRoot = root,
        Model = model,
        Prune = prune,
        Diagnostics = diagnostics,
    };
}
