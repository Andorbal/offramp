using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Dependencies.Consolidation;

namespace Offramp.Refactoring.Tests;

/// <summary>
/// ROADMAP M6 acceptance for <c>deps consolidate</c> on <c>versions</c>: one Newtonsoft.Json
/// except the pinned project, the family bump explained with a chain, and restore
/// verification that blocks an induced NU1605.
/// </summary>
public sealed class ConsolidationTests
{
    [Fact]
    [ProducesDiagnostic("OFR1203")]
    public async Task Versions_consolidates_to_one_newtonsoft_except_the_pinned_project()
    {
        using var repository = await FixtureRepository.CreateAsync("versions", git: false);
        var diagnostics = new DiagnosticBag();

        var plan = (await PlanAsync(repository.Path, diagnostics))!;

        var newtonsoft = plan.Result.Packages.Single(p => p.Id == "Newtonsoft.Json");
        Assert.Equal("13.0.3", newtonsoft.Selected);
        Assert.Equal(["src/Reporting/Reporting.csproj", "Contoso.Serialization 2.0.0", "Newtonsoft.Json >= 13.0.3"],
            new[] { newtonsoft.Deciding!.Project! }.Concat(newtonsoft.Deciding.Chain));
        Assert.Equal("src/Customer.Api/Customer.Api.csproj", Assert.Single(Assert.Single(newtonsoft.Pinned).Projects));
        Assert.Equal(
            [
                ("src/Billing/Billing.csproj", "11.0.2", "13.0.3"),
                ("src/Modern.App/Modern.App.csproj", "12.0.3", "13.0.3"),
                ("src/Shared/Shared.csproj", "13.0.1", "13.0.3"),
            ],
            newtonsoft.Changes.Select(c => (c.File, c.From!, c.To!)));
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR1203" && d.Project == "src/Customer.Api/Customer.Api.csproj");
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", After(plan.ChangeSet!, "src/Billing/Billing.csproj"), StringComparison.Ordinal);
        Assert.DoesNotContain(plan.ChangeSet!.Edits, e => e.Path == "src/Customer.Api/Customer.Api.csproj");
        Assert.Empty(plan.Result.Unsatisfiable);
    }

    [Fact]
    public async Task The_family_bump_is_explained_with_a_chain()
    {
        using var repository = await FixtureRepository.CreateAsync("versions", git: false);

        var plan = (await PlanAsync(repository.Path, new DiagnosticBag(), r => r with { Family = "Microsoft.Extensions." }))!;

        Assert.Equal(["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"], plan.Result.Packages.Select(p => p.Id));
        Assert.All(plan.Result.Packages, p => Assert.Equal(("8.0.0", "Microsoft.Extensions."), (p.Selected!, p.Family!)));
        var injection = plan.Result.Packages[0];
        Assert.Equal(
            "src/Reporting/Reporting.csproj: Microsoft.Extensions.Logging.Abstractions 8.0.0 → Microsoft.Extensions.DependencyInjection.Abstractions >= 8.0.0.",
            injection.Reason);
        Assert.Equal(("src/Modern.App/Modern.App.csproj", "6.0.0", "8.0.0"), (injection.Changes[0].File, injection.Changes[0].From!, injection.Changes[0].To!));
    }

    [Fact]
    [ProducesDiagnostic("OFR1210")]
    [ProducesDiagnostic("OFR1212")]
    public async Task A_pin_below_a_transitive_bound_is_refused_with_its_chain()
    {
        using var repository = await FixtureRepository.CreateAsync("versions", git: false);
        var diagnostics = new DiagnosticBag();
        var config = Config(repository.Path);
        config = config with { Deps = config.Deps with { Pins = [new PackagePin { Package = "Newtonsoft.Json", Version = "12.0.3", Reason = "test" }] } };

        var plan = (await PlanAsync(repository.Path, diagnostics, r => r with { Config = config, Package = "Newtonsoft.Json" }))!;
        var stuck = new DiagnosticBag();
        var limited = config with { Deps = config.Deps with { Pins = [], IncludePrerelease = false } };
        var none = (await PlanAsync(repository.Path, stuck, r => r with { Config = limited, Package = "Contoso.Legacy.Reports", Model = WithTarget(r.Model, "src/Billing/Billing.csproj", "net10.0") }))!;

        var refused = Assert.Single(plan.Result.Unsatisfiable);
        Assert.Equal(("Newtonsoft.Json", "OFR1210"), (refused.Id, refused.Code));
        Assert.Equal(["Contoso.Serialization 2.0.0", "Newtonsoft.Json >= 13.0.3"], refused.Chain);
        Assert.Null(plan.ChangeSet);
        Assert.True(diagnostics.Contains("OFR1210"));
        Assert.Equal("OFR1212", Assert.Single(none.Result.Unsatisfiable).Code);
        Assert.True(stuck.Contains("OFR1212"));
    }

    [Fact]
    [ProducesDiagnostic("OFR1220")]
    public async Task A_family_member_without_the_family_version_keeps_its_own()
    {
        using var repository = await FixtureRepository.CreateAsync("versions", git: false);
        var diagnostics = new DiagnosticBag();
        var config = Config(repository.Path);
        config = config with { Deps = config.Deps with { Families = [new PackageFamily { Prefix = "Contoso." }] } };

        var plan = (await PlanAsync(repository.Path, diagnostics, r => r with { Config = config, Family = "Contoso." }))!;

        Assert.Equal(
            [("Contoso.Legacy.Reports", "1.1.0"), ("Contoso.Serialization", "2.0.0"), ("Contoso.Windows.Controls", "1.0.0")],
            plan.Result.Packages.Select(p => (p.Id, p.Selected!)));
        Assert.Equal(2, diagnostics.ToSortedList().Count(d => d.Code == "OFR1220"));
    }

    [Fact]
    [ProducesDiagnostic("OFR1200")]
    public async Task An_unreferenced_package_is_an_error()
    {
        using var repository = await FixtureRepository.CreateAsync("versions", git: false);
        var diagnostics = new DiagnosticBag();

        Assert.Null(await PlanAsync(repository.Path, diagnostics, r => r with { Package = "Not.A.Package" }));
        Assert.True(diagnostics.Contains("OFR1200"));
    }

    [Fact]
    public async Task Converting_to_central_management_writes_package_versions_and_overrides_the_pin()
    {
        using var repository = await FixtureRepository.CreateAsync("versions", git: false);

        var plan = (await PlanAsync(repository.Path, new DiagnosticBag(), r => r with { Cpm = true }))!;

        Assert.Equal(("convert", "Directory.Packages.props"), (plan.Result.Cpm!.Mode, plan.Result.Cpm.File));
        var central = After(plan.ChangeSet!, "Directory.Packages.props");
        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", central, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", central, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" VersionOverride=\"9.0.1\" />", After(plan.ChangeSet!, "src/Customer.Api/Customer.Api.csproj"), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" />", After(plan.ChangeSet!, "src/Billing/Billing.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR1211")]
    public async Task Restore_verification_passes_the_plan_and_blocks_an_induced_downgrade()
    {
        using var repository = await FixtureRepository.CreateAsync("versions");
        var git = new GitService(ProcessRunner.Instance);
        var plan = (await PlanAsync(repository.Path, new DiagnosticBag()))!;
        var induced = new ChangeSet();
        var reporting = File.ReadAllBytes(Path.Combine(repository.Path, "src", "Reporting", "Reporting.csproj"));
        var editor = ProjectFiles.ProjectFileEditor.Load(reporting);
        editor.AddPackageReference("Newtonsoft.Json", "12.0.3");
        induced.Edit("src/Reporting/Reporting.csproj", reporting, editor.Save());
        var diagnostics = new DiagnosticBag();

        var passed = await RestoreVerifier.VerifyAsync(Verify(repository.Path, plan.ChangeSet!, git, new DiagnosticBag()), TestContext.Current.CancellationToken);
        var blocked = await RestoreVerifier.VerifyAsync(Verify(repository.Path, induced, git, diagnostics), TestContext.Current.CancellationToken);

        Assert.True(passed.Passed, string.Join("\n", passed.Warnings));
        Assert.False(blocked.Passed);
        var warning = Assert.Single(blocked.Warnings);
        Assert.Equal(("NU1605", "src/Reporting/Reporting.csproj"), (warning.Code, warning.Project));
        Assert.Contains("Newtonsoft.Json", warning.Message, StringComparison.Ordinal);
        Assert.True(diagnostics.Contains("OFR1211"));
    }

    [Fact]
    public void Restore_output_is_parsed_into_repository_relative_warnings()
    {
        var root = Path.Combine(Path.GetTempPath(), "scratch");
        var output = $"""
            {Path.Combine(root, "src", "A", "A.csproj")} : warning NU1605: Detected package downgrade: X from 2.0.0 to 1.0.0. [{Path.Combine(root, "S.slnx")}]
            {Path.Combine(root, "src", "A", "A.csproj")} : warning NU1605:  A -> Y 1.0.0 -> X (>= 2.0.0) [{Path.Combine(root, "S.slnx")}]
            Build succeeded.
            {Path.Combine(root, "src", "A", "A.csproj")} : warning NU1605: Detected package downgrade: X from 2.0.0 to 1.0.0. [{Path.Combine(root, "S.slnx")}]
            {Path.Combine(root, "src", "A", "A.csproj")} : warning NU1605:  A -> Y 1.0.0 -> X (>= 2.0.0) [{Path.Combine(root, "S.slnx")}]
            {Path.Combine(root, "src", "B", "B.csproj")} : warning NU1603: Approximate best match.
            {Path.Combine(root, "src", "B", "B.csproj")} : error NU1102: Unable to find package Y with version (>= 9.0.0)
            """;

        var warnings = RestoreVerifier.Parse(output, root);

        Assert.Equal(
            [
                new RestoreWarning("NU1605", "src/A/A.csproj", "Detected package downgrade: X from 2.0.0 to 1.0.0.\nA -> Y 1.0.0 -> X (>= 2.0.0)"),
                new RestoreWarning("NU1102", "src/B/B.csproj", "Unable to find package Y with version (>= 9.0.0)"),
            ],
            warnings);
    }

    [Fact]
    [ProducesDiagnostic("OFR1301")]
    [ProducesDiagnostic("OFR1302")]
    [ProducesDiagnostic("OFR1303")]
    public async Task Hazards_give_the_central_file_its_own_name_and_the_projects_opt_in_and_restore_accepts_it()
    {
        var fixture = await ScannedFixtures.GetAsync("cpm-shadowing");
        var diagnostics = new DiagnosticBag();
        var request = new ConsolidateRequest
        {
            RepositoryRoot = fixture.Root,
            Model = fixture.Outcome.Model!,
            Config = new OfframpConfig(),
            Feeds = new RecordedPackageFeeds(VersionsFeed.Load()),
            Cache = NullCache.Instance,
            Diagnostics = diagnostics,
            Prefer = "lowest",
            Cpm = true,
        };

        var plan = (await Consolidator.PlanAsync(request, TestContext.Current.CancellationToken))!;

        Assert.Equal(
            [("OFR1301", "tools/Stray/Stray.csproj"), ("OFR1302", "src/Nested/Directory.Packages.props"), ("OFR1303", "src/Legacy/Legacy.csproj")],
            plan.Result.Hazards.Select(h => (h.Code, h.Path)));
        Assert.Equal(("convert", "CpmShadowing.Packages.props"), (plan.Result.Cpm!.Mode, plan.Result.Cpm.File));
        Assert.Equal(["src/Api/Api.csproj", "src/Worker/Worker.csproj"], plan.Result.Cpm.OptIn);
        Assert.Contains("<Import Project=\"$(MSBuildThisFileDirectory)..\\..\\CpmShadowing.Packages.props\" />", After(plan.ChangeSet!, "src/Api/Api.csproj"), StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />", After(plan.ChangeSet!, "CpmShadowing.Packages.props"), StringComparison.Ordinal);
        Assert.DoesNotContain(plan.ChangeSet!.Edits, e => e.Path.StartsWith("tools/", StringComparison.Ordinal) || e.Path == "Directory.Packages.props");
        Assert.True(diagnostics.Contains("OFR1301"));

        var verification = await RestoreVerifier.VerifyAsync(new RestoreVerifyRequest
        {
            RepositoryRoot = fixture.Root,
            Model = fixture.Outcome.Model!,
            Config = new OfframpConfig(),
            ChangeSet = plan.ChangeSet!,
            Mode = "restore",
            Git = new GitService(ProcessRunner.Instance),
            Processes = ProcessRunner.Instance,
            Diagnostics = new DiagnosticBag(),
        }, TestContext.Current.CancellationToken);

        Assert.True(verification.Passed, string.Join("\n", verification.Warnings));
    }

    private static Task<ConsolidationPlan?> PlanAsync(string root, DiagnosticBag diagnostics, Func<ConsolidateRequest, ConsolidateRequest>? customize = null)
    {
        var request = new ConsolidateRequest
        {
            RepositoryRoot = root,
            Model = FixtureModels.Load("versions"),
            Config = Config(root),
            Feeds = new RecordedPackageFeeds(VersionsFeed.Load()),
            Cache = NullCache.Instance,
            Diagnostics = diagnostics,
            Prefer = "lowest",
        };
        return Consolidator.PlanAsync(customize?.Invoke(request) ?? request, TestContext.Current.CancellationToken);
    }

    private static RestoreVerifyRequest Verify(string root, ChangeSet changeSet, IGitService git, DiagnosticBag diagnostics) => new()
    {
        RepositoryRoot = root,
        Model = FixtureModels.Load("versions"),
        Config = Config(root),
        ChangeSet = changeSet,
        Mode = "restore",
        Git = git,
        Processes = ProcessRunner.Instance,
        Diagnostics = diagnostics,
    };

    private static Core.Model.WorkspaceModel WithTarget(Core.Model.WorkspaceModel model, string project, string tfm) =>
        model with { Projects = [.. model.Projects.Select(p => p.Id == project ? p with { TargetFrameworks = [tfm] } : p)] };

    private static OfframpConfig Config(string root) => ConfigLoader.Load(new ConfigSources { RepositoryRoot = root }).Config;

    private static string After(ChangeSet changeSet, string path) =>
        System.Text.Encoding.UTF8.GetString(changeSet.Edits.FirstOrDefault(e => e.Path == path)?.After ?? changeSet.Creates.Single(c => c.Path == path).Content);
}
