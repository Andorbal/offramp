using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
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

    private static RedirectsRequest Request(string root, WorkspaceModel model, DiagnosticBag diagnostics, bool prune) => new()
    {
        RepositoryRoot = root,
        Model = model,
        Prune = prune,
        Diagnostics = diagnostics,
    };
}
