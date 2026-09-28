using System.Text.Json.Nodes;
using Offramp.Cli.Commands;
using Offramp.Core.Configuration;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Init;
using Spectre.Console.Testing;

namespace Offramp.Cli.Tests;

public sealed class InitCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Defaults_write_the_configuration_and_gitignore()
    {
        _cli.Repo.Write("src/Monolith.sln", "");

        var run = await _cli.RunAsync("init", "--defaults", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "init");
        Assert.Contains("solution: src/Monolith.sln", _cli.Repo.Read("offramp.yml"), StringComparison.Ordinal);
        Assert.Contains(".offramp/cache/", _cli.Repo.Read(".gitignore"), StringComparison.Ordinal);
        await Verify(Scrub.Envelope(run.Out, _cli.Repo.Path), extension: "json");
    }

    [Fact]
    public async Task Written_configuration_is_what_doctor_then_reads()
    {
        await _cli.RunAsync("init", "--defaults", "--target", "9");

        var doctor = await _cli.RunAsync("doctor", "--json");

        var header = JsonNode.Parse(doctor.Out)!["offramp"]!;
        Assert.Equal("net9.0", header["target"]!.GetValue<string>());
        Assert.Equal(0, JsonNode.Parse(doctor.Out)!["summary"]!["warnings"]!.GetValue<int>() - 1);
    }

    [Fact]
    public async Task Second_run_refuses_to_overwrite_and_exits_1()
    {
        await _cli.RunAsync("init", "--defaults");

        var run = await _cli.RunAsync("init", "--defaults");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("already exists", run.Out, StringComparison.Ordinal);
        Assert.Contains("OFR0030", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_replaces_an_existing_even_broken_file()
    {
        _cli.Repo.Write("offramp.yml", "target: [broken\n");

        var run = await _cli.RunAsync("init", "--defaults", "--force");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Replaced offramp.yml.", run.Out, StringComparison.Ordinal);
        Assert.True(ConfigLoader.Load(new ConfigSources { RepositoryRoot = _cli.Repo.Path }).IsValid);
    }

    [Fact]
    public async Task Dry_run_shows_a_diff_and_writes_nothing()
    {
        var run = await _cli.RunAsync("init", "--defaults", "--dry-run");

        Assert.Equal(0, run.ExitCode);
        Assert.False(_cli.Repo.Exists("offramp.yml"));
        Assert.False(_cli.Repo.Exists(".gitignore"));
        await Verify(Scrub.Text(run.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task On_a_terminal_init_interviews_and_writes_the_answers()
    {
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.Repo.Write("a/A.sln", "");
        _cli.Repo.Write("b/B.sln", "");
        var prompter = new ScriptedPrompter(new InitValues
        {
            Target = 9,
            Solution = "b/B.sln",
            VerifyMode = "none",
            CpmFile = "eng/Packages.props",
            Pins = [new PackagePin { Package = "log4net", Version = "2.0.15", Reason = "Ops-approved" }],
        });
        _cli.InitPrompter = _ => prompter;

        var run = await _cli.RunAsync("init");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(["a/A.sln", "b/B.sln"], prompter.Seen!.SolutionCandidates);
        var loaded = ConfigLoader.Load(new ConfigSources { RepositoryRoot = _cli.Repo.Path });
        Assert.True(loaded.IsValid);
        Assert.Equal(9, loaded.Config.Target);
        Assert.Equal("b/B.sln", loaded.Config.Solution);
        Assert.Equal("log4net", loaded.Config.Deps.Pins.Single().Package);
    }

    [Fact]
    public async Task The_terminal_interview_explains_each_question_and_checks_typed_answers()
    {
        _cli.Repo.Write("apps/Legacy/Legacy.sln", "");
        _cli.Repo.Write("apps/Legacy/Api/Api.csproj", "<Project />");
        _cli.Repo.Write("tools/Unrelated/Unrelated.csproj", "<Project />");
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Input.PushTextWithEnter("");                              // target: keep 10
        console.Input.PushKey(ConsoleKey.Enter);                          // solution: the detected one
        console.Input.PushKey(ConsoleKey.DownArrow);                      // verify: command
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("./build.sh --no-tests");
        console.Input.PushTextWithEnter("");                              // central file: the suggestion
        console.Input.PushTextWithEnter("y");
        console.Input.PushTextWithEnter("Newtonsoft Json 9");             // refused
        console.Input.PushTextWithEnter("Newtonsoft.Json");
        console.Input.PushTextWithEnter("latest");                        // refused
        console.Input.PushTextWithEnter("9.0.1");
        console.Input.PushTextWithEnter("apps/Legacy/Missing.csproj");    // refused
        console.Input.PushTextWithEnter("apps\\Legacy\\Api\\Api.csproj");
        console.Input.PushTextWithEnter("Customers depend on 9.x serialization");
        console.Input.PushTextWithEnter("n");
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.InitPrompter = _ => new SpectreInitPrompter(console);

        var run = await _cli.RunAsync("init");

        Assert.Equal(0, run.ExitCode);
        var screen = string.Join(' ', console.Output.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Offramp checks every change it writes", screen, StringComparison.Ordinal);
        Assert.Contains("build Build the projects a change touches with dotnet build (recommended)", screen, StringComparison.Ordinal);
        Assert.Contains("nothing is written now", screen, StringComparison.Ordinal);
        Assert.Contains("(apps/Legacy/Directory.Packages.props)", screen, StringComparison.Ordinal);
        Assert.Contains("Most people answer No", screen, StringComparison.Ordinal);
        Assert.Contains("That is not a NuGet package id", screen, StringComparison.Ordinal);
        Assert.Contains("That is not a version", screen, StringComparison.Ordinal);
        Assert.Contains("No project file at apps/Legacy/Missing.csproj", screen, StringComparison.Ordinal);
        var config = ConfigLoader.Load(new ConfigSources { RepositoryRoot = _cli.Repo.Path }).Config;
        Assert.Equal(("command", "./build.sh --no-tests"), (config.Verify.Mode, config.Verify.Command));
        Assert.Equal("apps/Legacy/Directory.Packages.props", config.Deps.Cpm.PathFor(config.Solution));
        var pin = Assert.Single(config.Deps.Pins);
        Assert.Equal(("Newtonsoft.Json", "9.0.1", "apps/Legacy/Api/Api.csproj"), (pin.Package, pin.Version, pin.Project));
    }

    [Fact]
    public async Task A_central_file_typed_at_the_root_stays_at_the_root_when_the_solution_is_in_a_folder()
    {
        _cli.Repo.Write("apps/Legacy/Legacy.sln", "");
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        console.Input.PushTextWithEnter("");
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("Legacy.Packages.props");
        console.Input.PushTextWithEnter("n");
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.InitPrompter = _ => new SpectreInitPrompter(console);

        var run = await _cli.RunAsync("init");

        Assert.Equal(0, run.ExitCode);
        var config = ConfigLoader.Load(new ConfigSources { RepositoryRoot = _cli.Repo.Path }).Config;
        Assert.Equal(("Legacy.Packages.props", "repo"), (config.Deps.Cpm.File, config.Deps.Cpm.Scope));
        Assert.Equal("Legacy.Packages.props", config.Deps.Cpm.PathFor(config.Solution));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_windows_only_steps_init_offers_the_compile_only_block(bool accept)
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("windows-only-build-steps"), _cli.Repo.Path);
        var scan = await _cli.RunAsync("scan", "--binlog", "msbuild.binlog");
        Assert.True(_cli.Repo.Exists(".offramp/workspace.json"), scan.ToString());
        var before = _cli.Repo.Read("Directory.Build.props");
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        var prompter = new ScriptedPrompter(new InitValues { Target = 10, Solution = "WindowsOnly.sln", VerifyMode = "build" })
        {
            AcceptCompileOnly = accept,
        };
        _cli.InitPrompter = _ => prompter;

        var run = await _cli.RunAsync("init");

        Assert.Equal(0, run.ExitCode);
        Assert.True(prompter.Offered is not null, run.ToString());
        Assert.Contains("+  <PropertyGroup Condition=", prompter.Offered!.Diff, StringComparison.Ordinal);
        var props = _cli.Repo.Read("Directory.Build.props");
        Assert.Equal(accept, props.Contains("<OfframpCompileOnly>true</OfframpCompileOnly>", StringComparison.Ordinal));
        Assert.Equal(accept, props != before);
    }

    [Fact]
    public async Task Defaults_never_add_the_compile_only_block()
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("windows-only-build-steps"), _cli.Repo.Path);
        await _cli.RunAsync("scan", "--binlog", "msbuild.binlog");
        var before = _cli.Repo.Read("Directory.Build.props");

        var run = await _cli.RunAsync("init", "--defaults", "--json");

        Assert.Equal(0, run.ExitCode);
        Assert.Null(JsonNode.Parse(run.Out)!["result"]!["compileOnlyFix"]);
        Assert.Equal(before, _cli.Repo.Read("Directory.Build.props"));
    }

    [Fact]
    public async Task Defaults_flag_skips_the_interview_even_on_a_terminal()
    {
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.InitPrompter = _ => throw new InvalidOperationException("must not prompt");

        var run = await _cli.RunAsync("init", "--defaults");

        Assert.Equal(0, run.ExitCode);
    }

    private sealed class ScriptedPrompter(InitValues answers) : IInitPrompter
    {
        public InitDetection? Seen { get; private set; }

        public CompileOnlyFix? Offered { get; private set; }

        public bool AcceptCompileOnly { get; init; }

        public InitValues Ask(InitDetection detection)
        {
            Seen = detection;
            return answers;
        }

        public bool OfferCompileOnlyBlock(CompileOnlyFix plan)
        {
            Offered = plan;
            return AcceptCompileOnly;
        }
    }
}
