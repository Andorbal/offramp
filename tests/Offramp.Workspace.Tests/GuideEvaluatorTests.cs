using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Guide;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

public sealed class GuideEvaluatorTests
{
    private static readonly string[] SetupDone = ["doctor"];

    [Fact]
    public void Without_a_model_only_doctor_is_open_and_everything_else_waits()
    {
        var status = GuideEvaluator.Evaluate(Facts(model: null, configExists: false), new GuideState());

        Assert.Equal("setup", status.Stage);
        Assert.Equal(["doctor"], status.Next);
        Assert.Equal("Waiting on: doctor.", status.Step("init").Note);
        Assert.Equal(GuideStepStatus.Blocked, status.Step("scan").Status);
        Assert.Equal(GuideStepStatus.Blocked, status.Step("compile-only").Status);
        Assert.Equal(GuideStepStatus.Blocked, status.Step("move-tests").Status);
        Assert.StartsWith("Comes after", status.Step("plan").Note, StringComparison.Ordinal);
        Assert.Equal(new GuideCounts(GuideCatalog.Steps.Count, 0, 0, 1, GuideCatalog.Steps.Count - 1, 0), status.Counts);
        Assert.Equal([GuideStageStatus.Current, GuideStageStatus.Upcoming, GuideStageStatus.Upcoming, GuideStageStatus.Upcoming],
            status.Stages.Select(s => s.Status));
    }

    [Fact]
    public void Observed_facts_count_without_records_and_scan_ignores_them()
    {
        var withModel = GuideEvaluator.Evaluate(Facts(ModelOf(Project("src/A/A.csproj"))), new GuideState());
        Assert.Equal(GuideStepStatus.Done, withModel.Step("init").Status);
        Assert.Equal(GuideStepStatus.Done, withModel.Step("scan").Status);
        Assert.Equal(["doctor"], withModel.Next);

        var skippedScan = new GuideState()
            .With(Record("doctor", GuideRecordStatus.Done))
            .With(Record("scan", GuideRecordStatus.Skipped));
        var withoutModel = GuideEvaluator.Evaluate(Facts(model: null), skippedScan);
        Assert.Equal(GuideStepStatus.Open, withoutModel.Step("scan").Status);
        Assert.Equal(["scan"], withoutModel.Next);
    }

    [Fact]
    public void After_setup_the_open_steps_of_the_next_stage_are_all_choices_in_catalog_order()
    {
        var status = GuideEvaluator.Evaluate(Facts(ModelOf(Project("src/A/A.csproj"))), Done(SetupDone));

        Assert.Equal("understand", status.Stage);
        Assert.Equal(["plan", "graph", "audit-api", "audit-behavior", "audit-serialization", "audit-native", "audit-dead-code", "report"], status.Next);
        Assert.Equal(GuideStageStatus.Done, status.Stages[0].Status);
        Assert.Equal(GuideStepStatus.NotApplicable, status.Step("deps-audit").Status);
        Assert.Equal(GuideStepStatus.Blocked, status.Step("codemods").Status);
    }

    [Fact]
    public void A_stale_model_brings_setup_back_until_the_next_scan()
    {
        var model = ModelOf(Project("src/A/A.csproj"));
        var stale = new Staleness(["src/A/A.csproj"], [], [], false);

        var status = GuideEvaluator.Evaluate(Facts(model) with { Staleness = stale }, Done([.. SetupDone, "plan", "graph"]));

        Assert.Equal("setup", status.Stage);
        Assert.Equal(["scan"], status.Next);
        Assert.Equal("The model is out of date: 1 changed (src/A/A.csproj).", status.Step("scan").Note);
        Assert.Equal(GuideStepStatus.Done, status.Step("plan").Status);
        Assert.Equal(GuideStepStatus.Blocked, status.Step("audit-api").Status);
    }

    [Fact]
    public void Each_fact_decides_whether_its_step_is_needed()
    {
        var plain = GuideEvaluator.Evaluate(Facts(ModelOf(Project("src/A/A.csproj") with { SdkStyle = true, Language = "csharp" })), Done(SetupDone));
        string[] factSteps = ["compile-only", "deps-audit", "web-inventory", "move-tests", "csproj-modernize", "deps-consolidate", "resolve-dlls", "config-convert", "service", "web-scaffold"];
        Assert.All(factSteps, id => Assert.Equal(GuideStepStatus.NotApplicable, plain.Step(id).Status));

        var legacy = Project("src/Legacy/Legacy.csproj", files: [("Vendor.Thing", "lib/Vendor.Thing.dll")]) with
        {
            SdkStyle = false,
            Language = "csharp",
            WindowsOnlyBuildSteps = ["sgen"],
            PackageReferences = [new PackageReferenceInfo { Id = "NUnit", Version = "3.14.0" }],
        };
        var web = Project("src/Web/Web.csproj", kind: ProjectKind.Web) with { SdkStyle = true };
        var service = Project("src/Svc/Svc.csproj", kind: ProjectKind.Service) with { SdkStyle = true };
        var model = ModelOf(legacy, web, service) with
        {
            Packages = new(StringComparer.Ordinal)
            {
                ["NUnit"] = new PackageUsage { Versions = new(StringComparer.Ordinal) { ["3.13.0"] = ["src/Web/Web.csproj"], ["3.14.0"] = ["src/Legacy/Legacy.csproj"] } },
            },
        };
        var facts = Facts(model) with { ProjectsWithConfigFile = new HashSet<string> { "src/Web/Web.csproj" } };

        var status = GuideEvaluator.Evaluate(facts, Done(SetupDone));

        Assert.All(factSteps, id => Assert.NotEqual(GuideStepStatus.NotApplicable, status.Step(id).Status));
        Assert.Equal(["src/Legacy/Legacy.csproj"], status.Step("move-tests").Projects.Select(p => p.Project));
        Assert.Equal(["src/Web/Web.csproj"], status.Step("config-convert").Projects.Select(p => p.Project));
        Assert.Equal(["src/Svc/Svc.csproj"], status.Step("service").Projects.Select(p => p.Project));
        Assert.Equal("offramp web scaffold --project src/Web/Web.csproj --new src/Web.Core", status.Step("web-scaffold").Projects.Single().Command);
    }

    [Fact]
    public void A_step_done_per_project_is_done_when_every_project_is_done_or_skipped()
    {
        var model = ModelOf(
            Project("src/Web/Web.csproj", kind: ProjectKind.Web),
            Project("src/Admin/Admin.csproj", kind: ProjectKind.Web));
        var state = Done(SetupDone).With(Record("web-inventory", GuideRecordStatus.Done, "src/Admin/Admin.csproj"));

        var one = GuideEvaluator.Evaluate(Facts(model), state).Step("web-inventory");
        Assert.Equal(GuideStepStatus.Open, one.Status);
        Assert.Equal("1 of 2 project(s) open.", one.Note);
        Assert.Equal([("src/Admin/Admin.csproj", GuideStepStatus.Done), ("src/Web/Web.csproj", GuideStepStatus.Open)], one.Projects.Select(p => (p.Project, p.Status)));
        Assert.Equal("offramp web inventory --project src/Web/Web.csproj", one.Projects[1].Command);

        var both = GuideEvaluator.Evaluate(Facts(model), state.With(Record("web-inventory", GuideRecordStatus.Skipped, "src/Web/Web.csproj")));
        Assert.Equal(GuideStepStatus.Done, both.Step("web-inventory").Status);

        var allSkipped = state.With(Record("web-inventory", GuideRecordStatus.Skipped));
        Assert.Equal([new GuideRecord { Step = "web-inventory", Status = GuideRecordStatus.Skipped }], allSkipped.Records.Where(r => r.Step == "web-inventory"));
        Assert.Equal(GuideStepStatus.Skipped, GuideEvaluator.Evaluate(Facts(model), allSkipped).Step("web-inventory").Status);
    }

    [Fact]
    public void Web_scaffold_waits_for_the_inventory()
    {
        var model = ModelOf(Project("src/Web/Web.csproj", kind: ProjectKind.Web));
        var state = Done([.. SetupDone, .. Understand, .. Prepare]);

        var waiting = GuideEvaluator.Evaluate(Facts(model), state.Without("web-inventory", null));
        Assert.Equal("understand", waiting.Stage);
        Assert.Equal(["web-inventory"], waiting.Next);

        var ready = GuideEvaluator.Evaluate(Facts(model), state);
        Assert.Equal("port", ready.Stage);
        Assert.Contains("web-scaffold", ready.Next);
    }

    [Fact]
    public void Port_offers_ready_projects_with_the_target_added_and_is_done_when_only_hosted_applications_are_left()
    {
        var core = Project("src/Core/Core.csproj") with { TargetFrameworks = ["net48"] };
        var data = Project("src/Data/Data.csproj", references: ["src/Core/Core.csproj"]) with { TargetFrameworks = ["net48"] };
        var desktop = Project("src/Desk/Desk.csproj", kind: ProjectKind.Winforms) with { TargetFrameworks = ["net472"] };
        var web = Project("src/Web/Web.csproj", references: ["src/Data/Data.csproj"], kind: ProjectKind.Web);
        var state = Done([.. SetupDone, .. Understand, .. Prepare]);

        var first = GuideEvaluator.Evaluate(Facts(ModelOf(core, data, desktop, web)), state).Step("port");
        Assert.Equal(GuideStepStatus.Open, first.Status);
        Assert.Equal(
            [
                "offramp csproj modernize --project src/Core/Core.csproj --tfm \"net48;net10.0\"",
                "offramp csproj modernize --project src/Desk/Desk.csproj --tfm \"net472;net10.0-windows\"",
            ],
            first.Projects.Select(p => p.Command));

        var ported = ModelOf(core with { FrameworkClass = FrameworkClass.Dual }, data with { FrameworkClass = FrameworkClass.Dual },
            desktop with { FrameworkClass = FrameworkClass.Dual }, web);
        var last = GuideEvaluator.Evaluate(Facts(ported), state);
        Assert.Equal(GuideStepStatus.Done, last.Step("port").Status);
        Assert.Equal(["web-scaffold"], last.Next);
    }

    [Fact]
    public void Port_is_blocked_with_a_reason_when_only_a_cycle_is_left()
    {
        var model = ModelOf(
            Project("legacy/A/A.csproj", references: ["legacy/B/B.csproj"]),
            Project("legacy/B/B.csproj", references: ["legacy/A/A.csproj"]));

        var status = GuideEvaluator.Evaluate(Facts(model), Done([.. SetupDone, .. Understand, .. Prepare]));

        Assert.Null(status.Stage);
        Assert.Empty(status.Next);
        Assert.Equal(GuideStepStatus.Blocked, status.Step("port").Status);
        Assert.StartsWith("2 .NET Framework project(s) remain but none is ready", status.Step("port").Note, StringComparison.Ordinal);
        Assert.Equal(GuideStageStatus.Upcoming, status.Stages[^1].Status);
    }

    [Fact]
    public void Previewed_and_failed_records_leave_a_step_open_with_a_note()
    {
        var model = ModelOf(Project("src/A/A.csproj") with { WindowsOnlyBuildSteps = ["com"] });

        var previewed = GuideEvaluator.Evaluate(Facts(model), Done(SetupDone).With(Record("compile-only", GuideRecordStatus.Previewed, exitCode: 0))).Step("compile-only");
        Assert.Equal(GuideStepStatus.Open, previewed.Status);
        Assert.Equal("Dry run shown; nothing changed yet. Apply with `offramp doctor --fix --apply`, or start the guide with --apply.", previewed.Note);

        var failed = GuideEvaluator.Evaluate(Facts(model), new GuideState().With(Record("doctor", GuideRecordStatus.Failed, exitCode: 1))).Step("doctor");
        Assert.Equal(GuideStepStatus.Open, failed.Status);
        Assert.StartsWith("Doctor found a failing check", failed.Note, StringComparison.Ordinal);

        var present = GuideEvaluator.Evaluate(Facts(model) with { CompileOnlyPresent = true }, Done(SetupDone)).Step("compile-only");
        Assert.Equal(GuideStepStatus.Done, present.Status);
    }

    [Fact]
    public void Titles_and_explanations_name_the_target()
    {
        var status = GuideEvaluator.Evaluate(Facts(model: null) with { Target = 9 }, new GuideState());

        Assert.Equal("Check your NuGet packages against net9.0", status.Step("deps-audit").Title);
        Assert.DoesNotContain(status.Stages.SelectMany(s => s.Steps), s => s.Why.Contains("{target}", StringComparison.Ordinal) || s.Title.Contains("{target}", StringComparison.Ordinal));
    }

    [Fact]
    public void The_catalog_has_unique_ids_and_requires_only_earlier_steps()
    {
        var seen = new List<string>();
        foreach (var step in GuideCatalog.Steps)
        {
            Assert.Matches("^[a-z][a-z0-9-]*$", step.Id);
            Assert.DoesNotContain(step.Id, seen);
            Assert.All(step.Requires, r => Assert.Contains(r, seen));
            Assert.Equal(step.PerProject, step.Command.Contains("{project}"));
            seen.Add(step.Id);
        }

        Assert.Equal(["setup", "understand", "prepare", "port"], GuideCatalog.Stages.Select(s => s.Id));
    }

    [Fact]
    public void The_progress_file_is_sorted_schema_valid_and_round_trips()
    {
        using var dir = new ScratchDirectory("guide-state");
        var path = GuideStateStore.PathIn(dir.Combine(".offramp"));
        var state = new GuideState()
            .With(Record("move-tests", GuideRecordStatus.Previewed, "src/B/B.csproj", 0))
            .With(Record("move-tests", GuideRecordStatus.Done, "src/A/A.csproj", 0))
            .With(Record("doctor", GuideRecordStatus.Done, exitCode: 0))
            .With(Record("audit-api", GuideRecordStatus.Skipped));

        GuideStateStore.Save(path, state);
        var text = File.ReadAllText(path);

        SchemaAssert.Valid("guide-state", text);
        Assert.Equal(["audit-api", "doctor", "move-tests", "move-tests"], state.Records.Select(r => r.Step));
        Assert.Equal(state.Records, GuideStateStore.Read(path)!.Records);
        Assert.Null(GuideStateStore.Read(dir.Combine("missing.json")));
        Assert.Equal(["src/A/A.csproj"], state.Without("move-tests", "src/B/B.csproj").Records.Where(r => r.Step == "move-tests").Select(r => r.Project));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"version\": 2, \"records\": [] }")]
    [InlineData("{ \"version\": 1, \"records\": [ { \"step\": \"\", \"status\": \"done\" } ] }")]
    public void An_unreadable_progress_file_is_reported_not_guessed(string content)
    {
        using var dir = new ScratchDirectory("guide-state");
        var path = dir.Combine("guide.json");
        File.WriteAllText(path, content);

        Assert.Throws<InvalidDataException>(() => GuideStateStore.Read(path));
    }

    private static readonly string[] Understand = [.. GuideCatalog.Stages[1].Steps.Select(s => s.Id)];

    private static readonly string[] Prepare = [.. GuideCatalog.Stages[2].Steps.Select(s => s.Id)];

    private static GuideFacts Facts(WorkspaceModel? model, bool configExists = true) => new()
    {
        Target = 10,
        ConfigExists = configExists,
        Model = model,
        Staleness = model is null ? null : new Staleness([], [], [], false),
        Standings = model is null ? new Dictionary<string, ProjectStanding>() : Readiness.Compute(model),
    };

    private static GuideState Done(IEnumerable<string> steps) =>
        steps.Aggregate(new GuideState(), (state, step) => state.With(Record(step, GuideRecordStatus.Done)));

    private static GuideRecord Record(string step, GuideRecordStatus status, string? project = null, int? exitCode = null) =>
        new() { Step = step, Project = project, Status = status, ExitCode = exitCode };

    private static WorkspaceModel ModelOf(params ProjectInfo[] projects) => new()
    {
        CreatedAt = "2026-09-25T20:11:04Z",
        RepositoryRoot = "/repo",
        Solution = "App.sln",
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Projects = [.. projects.OrderBy(p => p.Id, StringComparer.Ordinal)],
        Graph = GraphBuilder.Build(projects),
    };
}
