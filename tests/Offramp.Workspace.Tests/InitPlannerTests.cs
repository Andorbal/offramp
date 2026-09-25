using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Init;

namespace Offramp.Workspace.Tests;

public sealed class InitPlannerTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("init");

    public void Dispose() => _repo.Dispose();

    private InitDetection Detect(DiagnosticBag? bag = null) =>
        InitPlanner.Detect(_repo.Path, new OfframpConfig(), bag ?? new DiagnosticBag());

    [Fact]
    public void The_only_solution_is_chosen()
    {
        _repo.Write("src/Monolith.sln", "");
        _repo.Write("src/bin/Debug/Copied.sln", "");
        _repo.Write(".git/Hidden.sln", "");

        var detection = Detect();

        Assert.Equal("src/Monolith.sln", detection.Values.Solution);
        Assert.Equal(["src/Monolith.sln"], detection.SolutionCandidates);
    }

    [Fact]
    public void A_single_root_solution_wins_over_nested_ones_and_filters_are_never_chosen()
    {
        _repo.Write("Monolith.slnx", "");
        _repo.Write("samples/Sample.sln", "");
        _repo.Write("Core.slnf", "{}");

        var detection = Detect();

        Assert.Equal("Monolith.slnx", detection.Values.Solution);
        Assert.Equal(["Core.slnf", "Monolith.slnx", "samples/Sample.sln"], detection.SolutionCandidates);
    }

    [Fact]
    [ProducesDiagnostic("OFR0020")]
    public void Ambiguous_solutions_leave_the_setting_empty_with_a_warning()
    {
        _repo.Write("a/A.sln", "");
        _repo.Write("b/B.sln", "");
        var bag = new DiagnosticBag();

        var detection = Detect(bag);

        Assert.Null(detection.Values.Solution);
        var diagnostic = Assert.Single(bag.ToSortedList());
        Assert.Equal("OFR0020", diagnostic.Code);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Existing_directory_packages_props_is_detected()
    {
        _repo.Write("Directory.Packages.props", "<Project />");
        Assert.Equal("Directory.Packages.props", Detect().Values.CpmFile);
    }

    public static TheoryData<InitValues> ValueSets => new()
    {
        new InitValues(),
        new InitValues { Target = 9, Solution = "src/My App/App.sln", VerifyMode = "none", CpmFile = "eng/Packages.props" },
        new InitValues
        {
            Solution = "true",
            Pins =
            [
                new PackagePin { Package = "Newtonsoft.Json", Version = "9.0", Project = "src/Api/Api.csproj", Reason = "Says \"hi\"\\ok" },
                new PackagePin { Package = "log4net", Version = "2.0.15", Reason = "Ops: approved" },
            ],
        },
    };

    /// <summary>The check that can fail: whatever init writes must load back cleanly and unchanged.</summary>
    [Theory]
    [MemberData(nameof(ValueSets))]
    public void Rendered_configuration_round_trips_through_the_loader(InitValues values)
    {
        _repo.Write("offramp.yml", InitPlanner.Render(values));

        var loaded = ConfigLoader.Load(new ConfigSources { RepositoryRoot = _repo.Path });

        Assert.True(loaded.IsValid);
        Assert.Empty(loaded.Diagnostics);
        Assert.Equal(values.Target, loaded.Config.Target);
        Assert.Equal(values.Solution, loaded.Config.Solution);
        Assert.Equal(values.VerifyMode, loaded.Config.Verify.Mode);
        Assert.Equal(values.CpmFile, loaded.Config.Deps.Cpm.File);
        Assert.Equal(values.Pins, loaded.Config.Deps.Pins);
    }

    [Fact]
    public Task Rendered_configuration_matches_the_snapshot() =>
        Verify(InitPlanner.Render(new InitValues { Solution = "src/Monolith.sln" }), extension: "yml");

    [Fact]
    public void Apply_writes_config_and_gitignore_entries_without_duplicates()
    {
        _repo.Write(".gitignore", "bin/\n.offramp/cache/");
        var bag = new DiagnosticBag();

        var result = InitPlanner.Apply(_repo.Path, Detect(), new InitValues(), ".offramp", dryRun: false, force: false, interactive: false, bag);

        Assert.True(result.Written);
        Assert.Equal(InitPlanner.Render(new InitValues()), _repo.Read("offramp.yml"));
        Assert.Equal([".offramp/cache/"], result.Gitignore.Present);
        Assert.Equal([".offramp/journal/", ".offramp/verify/", ".offramp/*.binlog", ".offramp/*.complog"], result.Gitignore.Added);
        Assert.Equal(
            "bin/\n.offramp/cache/\n\n# Offramp state (the ledger stays committed; see offramp.yml paths.state)\n.offramp/journal/\n.offramp/verify/\n.offramp/*.binlog\n.offramp/*.complog\n",
            _repo.Read(".gitignore"));
        Assert.Equal(0, bag.Count);

        var again = InitPlanner.Apply(_repo.Path, Detect(), new InitValues(), ".offramp", dryRun: false, force: true, interactive: false, bag);
        Assert.True(again.Replaced);
        Assert.Empty(again.Gitignore.Added);
    }

    [Fact]
    [ProducesDiagnostic("OFR0030")]
    public void Apply_refuses_to_overwrite_without_force()
    {
        _repo.Write("offramp.yml", "target: 8\n");
        var bag = new DiagnosticBag();

        var result = InitPlanner.Apply(_repo.Path, Detect(), new InitValues(), ".offramp", dryRun: false, force: false, interactive: false, bag);

        Assert.False(result.Written);
        Assert.Equal("target: 8\n", _repo.Read("offramp.yml"));
        Assert.False(_repo.Exists(".gitignore"));
        Assert.Equal("OFR0030", Assert.Single(bag.ToSortedList()).Code);
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        var result = InitPlanner.Apply(_repo.Path, Detect(), new InitValues(), ".offramp", dryRun: true, force: false, interactive: false, new DiagnosticBag());

        Assert.False(result.Written);
        Assert.True(result.DryRun);
        Assert.False(_repo.Exists("offramp.yml"));
        Assert.False(_repo.Exists(".gitignore"));
        Assert.Equal(5, result.Gitignore.Added.Count);
    }

    [Theory]
    [InlineData("src/Monolith.sln", "src/Monolith.sln")]
    [InlineData("true", "\"true\"")]
    [InlineData("10", "\"10\"")]
    [InlineData("my app.sln", "\"my app.sln\"")]
    [InlineData("#x", "\"#x\"")]
    public void Scalars_are_quoted_only_when_needed(string value, string expected) =>
        Assert.Equal(expected, InitPlanner.Scalar(value));
}
