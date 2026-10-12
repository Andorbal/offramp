using NuGet.Configuration;
using NuGet.Versioning;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Scaffolding.Newtonsoft;

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

    private static string Package() =>
        Path.Combine(SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(AppContext.BaseDirectory)), "newtonsoft.json", "13.0.4", "newtonsoft.json.13.0.4.nupkg");
}
