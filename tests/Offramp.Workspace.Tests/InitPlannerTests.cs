using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Init;

namespace Offramp.Workspace.Tests;

public sealed class InitPlannerTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("init");

    public void Dispose() => _repo.Dispose();

    private Task<InitDetection> DetectAsync(DiagnosticBag? bag = null) =>
        InitPlanner.DetectAsync(_repo.Path, new OfframpConfig(), bag ?? new DiagnosticBag(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_only_solution_is_chosen()
    {
        _repo.Write("src/Monolith.sln", "");
        _repo.Write("src/bin/Debug/Copied.sln", "");
        _repo.Write(".git/Hidden.sln", "");

        var detection = await DetectAsync();

        Assert.Equal("src/Monolith.sln", detection.Values.Solution);
        Assert.Equal(["src/Monolith.sln"], detection.SolutionCandidates);
    }

    [Fact]
    public async Task A_single_root_solution_wins_over_nested_ones_and_filters_are_never_chosen()
    {
        _repo.Write("Monolith.slnx", "");
        _repo.Write("samples/Sample.sln", "");
        _repo.Write("Core.slnf", "{}");

        var detection = await DetectAsync();

        Assert.Equal("Monolith.slnx", detection.Values.Solution);
        Assert.Equal(["Core.slnf", "Monolith.slnx", "samples/Sample.sln"], detection.SolutionCandidates);
    }

    [Fact]
    [ProducesDiagnostic("OFR0020")]
    public async Task Ambiguous_solutions_leave_the_setting_empty_with_a_warning()
    {
        _repo.Write("a/A.sln", "");
        _repo.Write("b/B.sln", "");
        var bag = new DiagnosticBag();

        var detection = await DetectAsync(bag);

        Assert.Null(detection.Values.Solution);
        var diagnostic = Assert.Single(bag.ToSortedList());
        Assert.Equal("OFR0020", diagnostic.Code);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
    }

    /// <summary>SmartStoreNET P2: SmartStoreNET.sln (25 projects) contains SmartStoreNET.Minimal.sln's 10; it is the one.</summary>
    [Fact]
    [ProducesDiagnostic("OFR0023")]
    public async Task A_solution_that_contains_the_others_is_chosen_and_the_reason_given()
    {
        WriteSlnx("src/Store.slnx", "Core/Core.csproj", "Web/Web.csproj", "Plugins/Pay/Pay.csproj");
        WriteSlnx("src/Store.Minimal.slnx", "Core/Core.csproj", "Web/Web.csproj");
        var bag = new DiagnosticBag();

        var detection = await DetectAsync(bag);

        Assert.Equal("src/Store.slnx", detection.Values.Solution);
        var chosen = Assert.Single(bag.ToSortedList());
        Assert.Equal("OFR0023", chosen.Code);
        Assert.Equal("Chose src/Store.slnx among 2 solutions: it contains every project of src/Store.Minimal.slnx. Pass --solution or set `solution:` in offramp.yml for another.", chosen.Message);
    }

    /// <summary>Open Live Writer P2: writer.sln (29 projects) and three one-to-four-project utility solutions.</summary>
    [Fact]
    public async Task Otherwise_the_solution_with_the_most_projects_is_chosen()
    {
        WriteSlnx("src/managed/writer.slnx", "A/A.csproj", "B/B.csproj", "C/C.csproj");
        WriteSlnx("utilities/BlogRunner/BlogRunner.slnx", "Runner/Runner.csproj", "Gui/Gui.csproj");
        WriteSlnx("utilities/Culture/Culture.slnx", "Culture.csproj");
        var bag = new DiagnosticBag();

        var detection = await DetectAsync(bag);

        Assert.Equal("src/managed/writer.slnx", detection.Values.Solution);
        Assert.Contains("it has the most projects (3; next: utilities/BlogRunner/BlogRunner.slnx with 2)", Assert.Single(bag.ToSortedList()).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// NHibernate: NHibernate.Everything.sln has one project more than NHibernate.sln, but one of them is a Web Site project,
    /// which stops a build with .NET's MSBuild (MSB4249); the solution that builds is chosen.
    /// </summary>
    [Fact]
    public async Task A_solution_with_a_web_site_project_is_set_aside()
    {
        WriteSln("src/NHibernate.sln", ("NHibernate", @"NHibernate\NHibernate.csproj", CSharp), ("Test", @"Test\Test.csproj", CSharp), ("Setup", @"Setup\Setup.csproj", CSharp));
        WriteSln("src/NHibernate.Everything.sln",
            ("NHibernate", @"NHibernate\NHibernate.csproj", CSharp), ("Test", @"Test\Test.csproj", CSharp), ("Tool", @"Tool\Tool.csproj", CSharp),
            ("Example.Web", @"Example.Web\", WebSite));
        var bag = new DiagnosticBag();

        var detection = await DetectAsync(bag);

        Assert.Equal("src/NHibernate.sln", detection.Values.Solution);
        Assert.Contains("src/NHibernate.Everything.sln has a Web Site project, which .NET's MSBuild cannot build (MSB4249)", Assert.Single(bag.ToSortedList()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Solutions_of_the_same_size_are_a_tie()
    {
        WriteSlnx("a/A.slnx", "One/One.csproj", "Two/Two.csproj");
        WriteSlnx("b/B.slnx", "Three/Three.csproj", "Four/Four.csproj");
        var bag = new DiagnosticBag();

        var detection = await DetectAsync(bag);

        Assert.Null(detection.Values.Solution);
        var tie = Assert.Single(bag.ToSortedList());
        Assert.Equal("OFR0020", tie.Code);
        Assert.Contains("a/A.slnx has 2 projects, b/B.slnx has 2 projects", tie.Message, StringComparison.Ordinal);
    }

    private const string CSharp = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
    private const string WebSite = "E24C65DC-7377-472B-9ABA-BC803B73C61A";

    private void WriteSlnx(string path, params string[] projects) =>
        _repo.Write(path, "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n");

    private void WriteSln(string path, params (string Name, string Path, string Type)[] projects)
    {
        var body = new System.Text.StringBuilder("\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n");
        var index = 0;
        foreach (var (name, projectPath, type) in projects)
        {
            var id = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{00000000-0000-0000-0000-{++index:D12}}}");
            body.Append(System.Globalization.CultureInfo.InvariantCulture, $"Project(\"{{{type}}}\") = \"{name}\", \"{projectPath}\", \"{id}\"\nEndProject\n");
        }

        _repo.Write(path, body.Append("Global\nEndGlobal\n").ToString());
    }

    [Fact]
    public async Task Existing_directory_packages_props_is_detected_and_kept_at_the_root()
    {
        _repo.Write("Directory.Packages.props", "<Project />");
        _repo.Write("src/Monolith.sln", "");

        var values = (await DetectAsync()).Values;

        Assert.Equal(("Directory.Packages.props", "repo"), (values.CpmFile, values.CpmScope));
        Assert.Equal("Directory.Packages.props", new CpmConfig { File = values.CpmFile, Scope = values.CpmScope }.PathFor(values.Solution));
    }

    public static TheoryData<InitValues> ValueSets => new()
    {
        new InitValues(),
        new InitValues { Target = 9, Solution = "src/My App/App.sln", VerifyMode = "none", CpmFile = "eng/Packages.props" },
        new InitValues { VerifyMode = "command", VerifyCommand = "./build.sh --configuration \"Debug\"", CpmFile = "Packages.props", CpmScope = "repo" },
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
        Assert.Equal(values.VerifyCommand, loaded.Config.Verify.Command);
        Assert.Equal(values.CpmFile, loaded.Config.Deps.Cpm.File);
        Assert.Equal(values.CpmScope, loaded.Config.Deps.Cpm.Scope);
        Assert.Equal(values.Pins, loaded.Config.Deps.Pins);
    }

    [Fact]
    public Task Rendered_configuration_matches_the_snapshot() =>
        Verify(InitPlanner.Render(new InitValues { Solution = "src/Monolith.sln" }), extension: "yml");

    [Fact]
    public async Task Apply_writes_config_and_gitignore_entries_without_duplicates()
    {
        _repo.Write(".gitignore", "bin/\n.offramp/cache/");
        var bag = new DiagnosticBag();

        var result = InitPlanner.Apply(_repo.Path, await DetectAsync(), new InitValues(), ".offramp", dryRun: false, force: false, interactive: false, bag);

        Assert.True(result.Written);
        Assert.Equal(InitPlanner.Render(new InitValues()), _repo.Read("offramp.yml"));
        Assert.Equal([".offramp/cache/"], result.Gitignore.Present);
        Assert.Equal([".offramp/journal/", ".offramp/verify/", ".offramp/*.binlog", ".offramp/*.complog"], result.Gitignore.Added);
        Assert.Equal(
            "bin/\n.offramp/cache/\n\n# Offramp state (the ledger stays committed; see offramp.yml paths.state)\n.offramp/journal/\n.offramp/verify/\n.offramp/*.binlog\n.offramp/*.complog\n",
            _repo.Read(".gitignore"));
        Assert.Equal(0, bag.Count);

        var again = InitPlanner.Apply(_repo.Path, await DetectAsync(), new InitValues(), ".offramp", dryRun: false, force: true, interactive: false, bag);
        Assert.True(again.Replaced);
        Assert.Empty(again.Gitignore.Added);
    }

    [Fact]
    [ProducesDiagnostic("OFR0030")]
    public async Task Apply_refuses_to_overwrite_without_force()
    {
        _repo.Write("offramp.yml", "target: 8\n");
        var bag = new DiagnosticBag();

        var result = InitPlanner.Apply(_repo.Path, await DetectAsync(), new InitValues(), ".offramp", dryRun: false, force: false, interactive: false, bag);

        Assert.False(result.Written);
        Assert.Equal("target: 8\n", _repo.Read("offramp.yml"));
        Assert.False(_repo.Exists(".gitignore"));
        Assert.Equal("OFR0030", Assert.Single(bag.ToSortedList()).Code);
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var result = InitPlanner.Apply(_repo.Path, await DetectAsync(), new InitValues(), ".offramp", dryRun: true, force: false, interactive: false, new DiagnosticBag());

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
