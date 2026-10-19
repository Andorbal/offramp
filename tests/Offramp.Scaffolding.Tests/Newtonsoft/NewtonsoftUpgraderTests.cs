using NuGet.Configuration;
using NuGet.Versioning;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Scaffolding.Newtonsoft;
using Offramp.Refactoring.Dependencies.Redirects;

namespace Offramp.Scaffolding.Tests.Newtonsoft;

/// <summary><c>newtonsoft upgrade</c>'s plan, without applying it.</summary>
public sealed class NewtonsoftUpgraderTests
{
    [Fact]
    [ProducesDiagnostic("OFR3765")]
    public async Task A_package_whose_dependency_leaves_out_the_target_is_named()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        var model = scanned.Outcome.Model!;
        var constrained = model with
        {
            Projects = [.. model.Projects.Select(p => p.Id != "src/Catalog/Catalog.csproj" ? p : p with
            {
                Resolved = new SortedDictionary<string, ResolvedFramework>(p.Resolved.ToDictionary(r => r.Key, r => r.Value with
                {
                    Packages = [.. r.Value.Packages, new ResolvedPackage { Id = "Contoso.Legacy.Client", Version = "2.1.0", Dependencies = [new PackageDependency("Newtonsoft.Json", "[6.0.8, 8.0.0)")] }],
                }), StringComparer.Ordinal),
            })],
        };
        var diagnostics = new DiagnosticBag();

        var (result, changeSet) = await NewtonsoftUpgrader.PlanAsync(new NewtonsoftUpgradeRequest
        {
            RepositoryRoot = scanned.Root,
            Model = constrained,
            Config = new OfframpConfig(),
            Target = NuGetVersion.Parse("13.0.4"),
            TargetPackage = File.ReadAllBytes(Package()),
            Processes = ProcessRunner.Instance,
            Git = new GitService(ProcessRunner.Instance),
            Diagnostics = diagnostics,
        }, CancellationToken.None);

        var warning = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR3765");
        Assert.Equal("src/Catalog/Catalog.csproj", warning.Project);
        Assert.Contains("Contoso.Legacy.Client 2.1.0", warning.Message, StringComparison.Ordinal);
        Assert.NotNull(changeSet);
        Assert.Contains(result.Projects, p => p.Project == "src/Catalog/Catalog.csproj");
    }

    [Fact]
    public async Task Redirects_are_scoped_to_changing_deployments_and_preserve_correct_destinations()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-legacy");
        var configPath = Path.Combine(scanned.Root, "src", "Orders.App", "App.config");
        var originalConfig = File.ReadAllBytes(configPath);
        var request = new NewtonsoftUpgradeRequest
        {
            RepositoryRoot = scanned.Root,
            Model = scanned.Outcome.Model!,
            Config = new OfframpConfig(),
            Target = NuGetVersion.Parse("13.0.4"),
            TargetPackage = File.ReadAllBytes(Package()),
            Processes = ProcessRunner.Instance,
            Git = new GitService(ProcessRunner.Instance),
            Diagnostics = new DiagnosticBag(),
        };

        var unchangedRelease = scanned.Outcome.Model!.Projects.Single(p => p.Id == "src/Orders.App/Orders.App.csproj")
            .PackagesConfigPackages!.Select(package => package.Id == "Newtonsoft.Json" ? package with { Version = "13.0.3" } : package).ToList();
        var alreadyTarget = request with
        {
            Model = request.Model with
            {
                Projects = [.. request.Model.Projects.Select(project => project.Id != "src/Orders.App/Orders.App.csproj" ? project : project with
                {
                    PackagesConfigPackages = unchangedRelease,
                })],
            },
        };
        File.WriteAllText(configPath, System.Text.Encoding.UTF8.GetString(originalConfig)
            .Replace("oldVersion=\"0.0.0.0-7.0.0.0\"", "oldVersion=\"0.0.0.0-9999.0.0.0\"", StringComparison.Ordinal));
        var (leftAloneResult, leftAloneSet) = await NewtonsoftUpgrader.PlanAsync(alreadyTarget, CancellationToken.None);
        Assert.DoesNotContain(leftAloneResult.Redirects!.Apps, a => a.Project == "src/Orders.App/Orders.App.csproj");
        Assert.DoesNotContain(leftAloneSet!.Edits, e => e.Path == "src/Orders.App/App.config");
        Assert.Contains("0.0.0.0-9999.0.0.0", File.ReadAllText(configPath), StringComparison.Ordinal);

        try
        {
            var (changedResult, changedSet) = await NewtonsoftUpgrader.PlanAsync(request, CancellationToken.None);
            var app = Assert.Single(changedResult.Redirects!.Apps, a => a.Project == "src/Orders.App/Orders.App.csproj");
            Assert.Equal(RedirectAction.Changed, Assert.Single(app.Redirects).Action);
            Assert.Contains(changedSet!.Edits, e => e.Path == "src/Orders.App/App.config");

            var withoutRedirect = System.Text.Encoding.UTF8.GetString(originalConfig)
                .Replace("      <dependentAssembly>\n        <assemblyIdentity name=\"Newtonsoft.Json\" publicKeyToken=\"30ad4fe6b2a6aeed\" culture=\"neutral\" />\n        <bindingRedirect oldVersion=\"0.0.0.0-7.0.0.0\" newVersion=\"7.0.0.0\" />\n      </dependentAssembly>\n", "", StringComparison.Ordinal);
            File.WriteAllText(configPath, withoutRedirect);
            var (addedResult, addedSet) = await NewtonsoftUpgrader.PlanAsync(request, CancellationToken.None);
            var addedApp = Assert.Single(addedResult.Redirects!.Apps, a => a.Project == "src/Orders.App/Orders.App.csproj");
            Assert.Equal(RedirectAction.Added, Assert.Single(addedApp.Redirects).Action);
            Assert.Contains(addedSet!.Edits, e => e.Path == "src/Orders.App/App.config");

            File.WriteAllText(configPath, System.Text.Encoding.UTF8.GetString(originalConfig)
                .Replace("newVersion=\"7.0.0.0\"", "newVersion=\"13.0.0.0\"", StringComparison.Ordinal));
            var (correctResult, correctSet) = await NewtonsoftUpgrader.PlanAsync(request, CancellationToken.None);
            var correctApp = Assert.Single(correctResult.Redirects!.Apps, a => a.Project == "src/Orders.App/Orders.App.csproj");
            var correctRedirect = Assert.Single(correctApp.Redirects);
            Assert.Equal(RedirectAction.Unchanged, correctRedirect.Action);
            Assert.Equal("0.0.0.0-7.0.0.0", correctRedirect.OldVersion);
            Assert.DoesNotContain(correctSet!.Edits, e => e.Path == "src/Orders.App/App.config");
        }
        finally
        {
            File.WriteAllBytes(configPath, originalConfig);
        }
    }

    private static string Package() =>
        Path.Combine(SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(AppContext.BaseDirectory)), "newtonsoft.json", "13.0.4", "newtonsoft.json.13.0.4.nupkg");
}
