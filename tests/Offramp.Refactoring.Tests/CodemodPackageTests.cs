using Offramp.Analysis.Compilations;
using Offramp.Analyzers.CodeFixes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Refactoring.Codemods;

namespace Offramp.Refactoring.Tests;

/// <summary>
/// A codemod whose rewrite needs a package leaves a project alone when the project cannot use the
/// package: rewritten code without its package does not compile (NHibernate field test, P1 #6).
/// </summary>
public sealed class CodemodPackageTests
{
    [Fact]
    [ProducesDiagnostic("OFR4512")]
    public async Task Outside_windows_a_legacy_project_keeps_the_sites_of_a_codemod_that_needs_a_package()
    {
        var diagnostics = new DiagnosticBag();

        var plan = await PlanAsync(shop => shop with { SdkStyle = false }, diagnostics, onWindows: false);
        var onWindows = await PlanAsync(shop => shop with { SdkStyle = false }, new DiagnosticBag(), onWindows: true);

        var project = Assert.Single(plan.Result.Projects);
        Assert.NotEmpty(project.Sites);
        Assert.All(project.Sites, s => Assert.Equal(CodemodSiteOutcome.Skipped, s.Outcome));
        Assert.Empty(project.Packages);
        Assert.True(plan.ChangeSet.IsEmpty);
        var legacy = Assert.Single(diagnostics.ToSortedList(), d => d.Code == "OFR4512");
        Assert.StartsWith("sqlclient needs Microsoft.Data.SqlClient 7.1.0, but src/Shop/Shop.csproj is a legacy (non-SDK) project", legacy.Message, StringComparison.Ordinal);
        Assert.Contains("offramp csproj modernize", legacy.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics.ToSortedList(), d => d.Code is "OFR4501" or "OFR4510");
        Assert.Contains(Assert.Single(onWindows.Result.Projects).Sites, s => s.Outcome == CodemodSiteOutcome.Rewritten);
    }

    internal static async Task<CodemodPlan> PlanAsync(Func<ProjectInfo, ProjectInfo> shop, DiagnosticBag diagnostics, bool onWindows, Func<CodemodRequest, CodemodRequest>? customize = null)
    {
        var fixture = await ScannedFixtures.GetAsync("codemods");
        var model = fixture.Outcome.Model!;
        using var loader = new CompilationLoader(fixture.Root);
        var request = new CodemodRequest
        {
            RepositoryRoot = fixture.Root,
            Model = model,
            Projects = [shop(model.Projects.Single(p => p.Name == "Shop"))],
            Codemods = [.. CodemodRegistry.All.Where(i => i.Codemod.Name == "sqlclient")],
            Loader = loader,
            Diagnostics = diagnostics,
            OnWindows = onWindows,
        };
        return await CodemodRunner.PlanAsync(customize?.Invoke(request) ?? request, TestContext.Current.CancellationToken);
    }
}
