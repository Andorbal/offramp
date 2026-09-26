using System.Text.Json.Nodes;
using Offramp.Cli.Commands;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Guide;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Offramp.Cli.Tests;

public sealed class GuideCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task First_run_runs_doctor_creates_the_progress_file_and_matches_the_snapshot()
    {
        var run = await _cli.RunAsync("guide", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "guide");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.True(result["started"]!.GetValue<bool>());
        var ran = result["ran"]!.AsArray().Single()!;
        Assert.Equal("doctor", ran["step"]!.GetValue<string>());
        Assert.Equal("done", ran["outcome"]!.GetValue<string>());
        SchemaAssert.ValidEnvelope(ran["envelope"]!.ToJsonString(), "doctor");
        Assert.Equal(["init"], Next(run));
        SchemaAssert.Valid("guide-state", _cli.Repo.Read(".offramp/guide.json"));
        await Verify(ScrubGuide(run.Out), extension: "json");
    }

    [Fact]
    public async Task Without_a_terminal_the_guide_reports_where_things_stand()
    {
        var first = await _cli.RunAsync("guide");

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Doctor passed", first.Out, StringComparison.Ordinal);
        var guide = first.Out[first.Out.IndexOf("offramp guide ─", StringComparison.Ordinal)..];
        await Verify(Scrub.Text(guide, _cli.Repo.Path), extension: "txt");

        var second = await _cli.RunAsync("guide", "--json");
        var result = JsonNode.Parse(second.Out)!["result"]!;
        Assert.False(result["started"]!.GetValue<bool>());
        Assert.Empty(result["ran"]!.AsArray());
        Assert.DoesNotContain("Doctor passed", (await _cli.RunAsync("guide")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Done_skip_and_reset_change_what_comes_next()
    {
        var done = await _cli.RunAsync("guide", "--done", "doctor", "--json");
        Assert.Empty(JsonNode.Parse(done.Out)!["result"]!["ran"]!.AsArray());
        Assert.Equal(["init"], Next(done));

        Assert.Equal(["scan"], Next(await _cli.RunAsync("guide", "--skip", "init", "--json")));
        Assert.Equal(["init"], Next(await _cli.RunAsync("guide", "--reset", "init", "--json")));

        var reset = await _cli.RunAsync("guide", "--reset", "all", "--json");
        Assert.Equal(["doctor"], Next(reset));
        Assert.Contains("\"records\": []", _cli.Repo.Read(".offramp/guide.json"), StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR0043")]
    public async Task Scan_cannot_be_skipped_or_marked_done()
    {
        await _cli.RunAsync("guide", "--done", "doctor");
        var before = _cli.Repo.Read(".offramp/guide.json");

        var run = await _cli.RunAsync("guide", "--skip", "scan", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR0043", run.Out, StringComparison.Ordinal);
        Assert.Equal(before, _cli.Repo.Read(".offramp/guide.json"));
        Assert.Equal(2, (await _cli.RunAsync("guide", "--done", "scan")).ExitCode);
    }

    [Fact]
    [ProducesDiagnostic("OFR0040")]
    public async Task An_unreadable_progress_file_stops_the_guide_until_reset_all()
    {
        _cli.Repo.Write(".offramp/guide.json", "{ \"records\": [ oops");

        var run = await _cli.RunAsync("guide", "--json");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("\"OFR0040\"", run.Out, StringComparison.Ordinal);
        Assert.Equal("{ \"records\": [ oops", _cli.Repo.Read(".offramp/guide.json"));

        Assert.Equal(0, (await _cli.RunAsync("guide", "--reset", "all")).ExitCode);
        SchemaAssert.Valid("guide-state", _cli.Repo.Read(".offramp/guide.json"));
    }

    [Fact]
    [ProducesDiagnostic("OFR0042")]
    public async Task A_step_that_fails_stays_open_with_a_warning()
    {
        _cli.Machine.SelectedSdk = "8.0.404";

        var run = await _cli.RunAsync("guide", "--json");

        Assert.Equal(0, run.ExitCode);
        var envelope = JsonNode.Parse(run.Out)!;
        var warning = envelope["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == "OFR0042")!;
        Assert.Equal("warning", warning["severity"]!.GetValue<string>());
        Assert.Equal("failed", envelope["result"]!["ran"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal(["doctor"], Next(run));
        Assert.Equal(1, (await _cli.RunAsync("guide", "--run", "doctor", "--fail-on", "warning")).ExitCode);
    }

    [Fact]
    public async Task Run_with_json_embeds_the_step_envelope_and_records_the_step()
    {
        Prepare(Library("src/Core/Core.csproj"), Library("src/App/App.csproj", references: ["src/Core/Core.csproj"]));

        var run = await _cli.RunAsync("guide", "--run", "plan", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "guide");
        var ran = JsonNode.Parse(run.Out)!["result"]!["ran"]![0]!;
        Assert.Equal("offramp plan --waves", ran["command"]!.GetValue<string>());
        SchemaAssert.ValidEnvelope(ran["envelope"]!.ToJsonString(), "plan");
        Assert.Equal(2, ran["envelope"]!["result"]!["order"]!.AsArray().Count);
        Assert.Contains(Records(), r => r is { Step: "plan", Status: GuideRecordStatus.Done, ExitCode: 0 });
        Assert.DoesNotContain("plan", Next(run));
    }

    [Fact]
    [ProducesDiagnostic("OFR0041")]
    public async Task A_step_done_per_project_needs_a_project_when_several_are_open()
    {
        Prepare(Web("src/Shop/Shop.csproj"), Web("src/Admin/Admin.csproj"));

        var run = await _cli.RunAsync("guide", "--run", "web-inventory", "--json");

        Assert.Equal(2, run.ExitCode);
        var diagnostic = JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Single()!;
        Assert.Equal("OFR0041", diagnostic["code"]!.GetValue<string>());
        Assert.Equal(["src/Admin/Admin.csproj", "src/Shop/Shop.csproj"], diagnostic["data"]!["candidates"]!.AsArray().Select(c => c!.GetValue<string>()));

        Assert.Equal(0, (await _cli.RunAsync("guide", "--skip", "web-inventory", "--project", "Admin")).ExitCode);
        Assert.Contains(Records(), r => r is { Step: "web-inventory", Project: "src/Admin/Admin.csproj", Status: GuideRecordStatus.Skipped });
        Assert.Equal(2, (await _cli.RunAsync("guide", "--done", "web-inventory", "--project", "Nowhere")).ExitCode);
        Assert.Equal(2, (await _cli.RunAsync("guide", "--done", "plan", "--project", "Admin")).ExitCode);
    }

    [Fact]
    public async Task A_writer_step_is_a_dry_run_until_apply_and_its_change_brings_back_the_scan()
    {
        Prepare(Library("src/Soap/Soap.csproj") with { WindowsOnlyBuildSteps = ["sgen"] });
        await _cli.RunAsync("guide", "--done", "doctor");

        var preview = await _cli.RunAsync("guide", "--run", "compile-only", "--json");

        Assert.Equal(0, preview.ExitCode);
        Assert.False(_cli.Repo.Exists("Directory.Build.props"));
        Assert.Contains(Records(), r => r is { Step: "compile-only", Status: GuideRecordStatus.Previewed });
        Assert.Equal(["compile-only"], Next(preview));

        var apply = await _cli.RunAsync("guide", "--run", "compile-only", "--apply", "--json");

        Assert.Equal(0, apply.ExitCode);
        Assert.Contains("<OfframpCompileOnly>true</OfframpCompileOnly>", _cli.Repo.Read("Directory.Build.props"), StringComparison.Ordinal);
        Assert.Equal("offramp doctor --fix --apply", JsonNode.Parse(apply.Out)!["result"]!["ran"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(["scan"], Next(apply));
        Assert.StartsWith("The model is out of date", Step(apply, "scan")["note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_asks_when_there_is_a_choice_and_saves_every_answer()
    {
        Prepare(Library("src/Core/Core.csproj") with { SdkStyle = true, TargetFrameworks = ["net48"] });
        var prompter = new ScriptedPrompter(
            GuideAnswer.Run, "plan",   // several steps open in "See what you have": pick plan
            GuideAnswer.Run,           // and run it
            GuideAnswer.SkipStage,     // skip the rest of the stage
            GuideAnswer.Skip,          // codemods, the only open step of "Tidy up": skip it
            GuideAnswer.Quit);         // port: stop for now
        OnTerminal(prompter);

        var run = await _cli.RunAsync("guide");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Welcome to the Offramp guide", prompter.Told[0], StringComparison.Ordinal);
        Assert.Equal(["plan"], prompter.Actions.Take(1).Select(a => a.Step));
        Assert.Equal([GuideAnswer.Run, GuideAnswer.MarkDone, GuideAnswer.Skip, GuideAnswer.Back, GuideAnswer.Quit], prompter.Actions[0].Answers);
        Assert.Equal(("codemods", (string?)null), (prompter.Actions[1].Step, prompter.Actions[1].Project));
        Assert.Equal([GuideAnswer.Run, GuideAnswer.MarkDone, GuideAnswer.Skip, GuideAnswer.Quit], prompter.Actions[1].Answers);
        Assert.Equal(("port", "src/Core/Core.csproj"), (prompter.Actions[2].Step, prompter.Actions[2].Project));
        Assert.Equal("offramp csproj modernize --project src/Core/Core.csproj --tfm \"net48;net10.0\"", prompter.Actions[2].Command);

        var records = Records();
        Assert.Contains(records, r => r is { Step: "doctor", Status: GuideRecordStatus.Done });
        Assert.Contains(records, r => r is { Step: "plan", Status: GuideRecordStatus.Done });
        Assert.Contains(records, r => r is { Step: "audit-api", Status: GuideRecordStatus.Skipped });
        Assert.Contains(records, r => r is { Step: "codemods", Status: GuideRecordStatus.Skipped });
        Assert.DoesNotContain(records, r => r.Step == "port");
        Assert.Contains("Progress is saved in .offramp/guide.json", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_session_applies_only_with_apply_and_a_yes(bool apply, bool confirm)
    {
        Prepare(Library("src/Soap/Soap.csproj") with { WindowsOnlyBuildSteps = ["com"] });
        await _cli.RunAsync("guide", "--done", "doctor");
        var prompter = new ScriptedPrompter(GuideAnswer.Run, GuideAnswer.Quit) { Confirm = confirm };
        OnTerminal(prompter);

        var run = await _cli.RunAsync(apply ? ["guide", "--apply"] : ["guide"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(apply, prompter.Confirmations.Count == 1);
        var applied = apply && confirm;
        Assert.Equal(applied, _cli.Repo.Exists("Directory.Build.props"));
        Assert.Equal(applied ? "scan" : "compile-only", prompter.Actions[1].Step);
        Assert.Equal(applied ? [GuideAnswer.Run, GuideAnswer.Quit] : [GuideAnswer.Run, GuideAnswer.MarkDone, GuideAnswer.Skip, GuideAnswer.Quit], prompter.Actions[1].Answers);
        if (!apply)
        {
            Assert.Contains(prompter.Told, t => t.StartsWith("That was a dry run; nothing changed.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_terminal_prompter_is_driven_by_keys()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.Input.PushKey(ConsoleKey.DownArrow);   // init: "Mark it done"
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.DownArrow);   // scan: "Stop for now"
        console.Input.PushKey(ConsoleKey.Enter);
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.GuidePrompter = _ => new SpectreGuidePrompter(console);

        var run = await _cli.RunAsync("guide");

        Assert.Equal(0, run.ExitCode);
        var screen = console.Output;
        Assert.Contains("Welcome to the Offramp guide", screen, StringComparison.Ordinal);
        Assert.Contains("offramp.yml records the target .NET version", screen, StringComparison.Ordinal);
        Assert.Contains("Command: offramp scan", screen, StringComparison.Ordinal);
        Assert.Contains(Records(), r => r is { Step: "init", Status: GuideRecordStatus.Done, ExitCode: null });
        Assert.False(_cli.Repo.Exists("offramp.yml"));
    }

    [Fact]
    public async Task Without_cursor_control_the_terminal_prompter_asks_for_a_number()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 120;
        console.Profile.Capabilities.Ansi = false;
        console.Input.PushTextWithEnter("9");   // out of range: asked again
        console.Input.PushTextWithEnter("3");   // init: "Skip it"
        console.Input.PushTextWithEnter("2");   // scan: "Stop for now"
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.GuidePrompter = _ => new SpectreGuidePrompter(console);

        var run = await _cli.RunAsync("guide");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("  3) Skip it", console.Output, StringComparison.Ordinal);
        Assert.Contains("Pick 1 to 4.", console.Output, StringComparison.Ordinal);
        Assert.Contains(Records(), r => r is { Step: "init", Status: GuideRecordStatus.Skipped });
    }

    [Fact]
    public void Every_step_command_parses_as_an_offramp_command_line()
    {
        var root = OfframpCli.BuildRoot(_cli.Host(TextWriter.Null, TextWriter.Null));
        var project = Web("src/Shop/Shop.csproj") with { TargetFrameworks = ["net48"] };

        foreach (var step in GuideCatalog.Steps)
        {
            var parse = root.Parse([.. GuideCatalog.Arguments(step, step.PerProject ? project : null, 10)]);
            Assert.True(parse.Errors.Count == 0, $"{step.Id}: {string.Join("; ", parse.Errors.Select(e => e.Message))}");
            Assert.NotSame(root, parse.CommandResult.Command);
        }

        Assert.NotEmpty(root.Parse(["audit", "apis"]).Errors);
        Assert.NotEmpty(root.Parse(["web", "inventory"]).Errors);
    }

    private void Prepare(params ProjectInfo[] projects)
    {
        _cli.Repo.Write("offramp.yml", "version: 1\n");
        var state = _cli.Repo.Combine(".offramp");
        WorkspaceStore.Save(Path.Combine(state, "workspace.json"), new WorkspaceModel
        {
            CreatedAt = "2026-09-25T20:11:04Z",
            RepositoryRoot = _cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Sdk = new SdkInfo("10.0.100", "linux-x64"),
            Projects = [.. projects.OrderBy(p => p.Id, StringComparer.Ordinal)],
            Graph = GraphBuilder.Build(projects),
            Inputs = WorkspaceInputs.Collect(_cli.Repo.Path, state),
        });
    }

    private void OnTerminal(IGuidePrompter prompter)
    {
        _cli.InputIsTerminal = true;
        _cli.OutputIsTerminal = true;
        _cli.GuidePrompter = _ => prompter;
    }

    private static ProjectInfo Library(string id, string[]? references = null) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(id),
        Kind = ProjectKind.Library,
        SdkStyle = true,
        TargetFrameworks = ["net48"],
        FrameworkClass = FrameworkClass.Framework,
        ProjectReferences = references ?? [],
    };

    private static ProjectInfo Web(string id) => Library(id) with { Kind = ProjectKind.Web };

    private IReadOnlyList<GuideRecord> Records() => GuideStateStore.Read(_cli.Repo.Combine(".offramp", "guide.json"))!.Records;

    private static IReadOnlyList<string> Next(CliRun run) =>
        [.. JsonNode.Parse(run.Out)!["result"]!["next"]!.AsArray().Select(n => n!.GetValue<string>())];

    private static JsonNode Step(CliRun run, string id) =>
        JsonNode.Parse(run.Out)!["result"]!["stages"]!.AsArray().SelectMany(s => s!["steps"]!.AsArray()).Single(s => s!["id"]!.GetValue<string>() == id)!;

    /// <summary>The envelope with the embedded step envelopes reduced to their command, so the snapshot is the guide's own.</summary>
    private string ScrubGuide(string json)
    {
        var node = JsonNode.Parse(json)!;
        foreach (var run in node["result"]!["ran"]!.AsArray())
        {
            run!["envelope"] = $"{{Envelope of offramp {run["envelope"]!["offramp"]!["command"]!.GetValue<string>()}}}";
        }

        return Scrub.Envelope(node.ToJsonString(), _cli.Repo.Path);
    }

    /// <summary>Answers from a script: <see cref="GuideAnswer.Run"/> followed by a step id picks that step.</summary>
    private sealed class ScriptedPrompter(params object[] script) : IGuidePrompter
    {
        private readonly Queue<object> _script = new(script);

        public bool Confirm { get; init; }

        public List<string> Told { get; } = [];

        public List<string> Confirmations { get; } = [];

        public List<(string Step, string? Project, string Command, IReadOnlyList<GuideAnswer> Answers)> Actions { get; } = [];

        public void Show(GuideStatus status)
        {
        }

        public GuidePick PickStep(GuideStatus status)
        {
            var answer = (GuideAnswer)_script.Dequeue();
            return answer == GuideAnswer.Run ? new GuidePick(answer, (string)_script.Dequeue()) : new GuidePick(answer);
        }

        public string? PickProject(GuideStepReport step) => throw new InvalidOperationException("no project question expected");

        public GuideAnswer PickAction(GuideStepReport step, string? project, string command, IReadOnlyList<GuideAnswer> answers)
        {
            Actions.Add((step.Id, project, command, answers));
            return (GuideAnswer)_script.Dequeue();
        }

        public bool ConfirmApply(string command)
        {
            Confirmations.Add(command);
            return Confirm;
        }

        public void Tell(string message) => Told.Add(message);
    }
}
