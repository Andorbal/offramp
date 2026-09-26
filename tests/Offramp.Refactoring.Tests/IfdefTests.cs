using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Refactoring.Conditional;
using Offramp.Workspace.Store;

namespace Offramp.Refactoring.Tests;

public sealed class IfdefTests
{
    [Fact]
    public async Task Strip_keeps_the_branch_for_targets_without_the_symbol_and_removes_whole_lines_only()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;

        var plan = IfdefStripPlanner.Plan(Request(fixture, keep: false, new DiagnosticBag()));

        Assert.Equal(["src/Shared/Clock.cs", "src/Shared/Formatter.cs"], plan.Result.Files);
        Assert.Equal([("src/Shared/Clock.cs", 5, "else", 4), ("src/Shared/Formatter.cs", 10, "else", 4)],
            plan.Result.Stripped.Select(s => (s.File, s.Line, s.Kept, s.RemovedLines)));
        var formatter = Encoding(plan, "src/Shared/Formatter.cs");
        Assert.Equal(
            Before(repository, "src/Shared/Formatter.cs").Replace(
                "#if NETFRAMEWORK\n    public string Encode(string value) => System.Web.HttpUtility.UrlEncode(value);\n#else\n", "", StringComparison.Ordinal)
                .Replace("#endif\n", "", StringComparison.Ordinal),
            formatter);
    }

    [Fact]
    public async Task Strip_keeping_the_symbol_keeps_the_framework_branch()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;

        var plan = IfdefStripPlanner.Plan(Request(fixture, keep: true, new DiagnosticBag()));

        var clock = Encoding(plan, "src/Shared/Clock.cs");
        Assert.Contains("public static System.DateTime UtcNow() => System.DateTime.UtcNow;", clock, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeProvider", clock, StringComparison.Ordinal);
        Assert.DoesNotContain("#", clock, StringComparison.Ordinal);
        Assert.Equal("if", plan.Result.Stripped[0].Kept);
    }

    [Fact]
    [ProducesDiagnostic("OFR3603")]
    public async Task A_region_that_also_depends_on_other_symbols_stays()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        var clock = repository.Directory.Read("src/Shared/Clock.cs");
        repository.Directory.Write("src/Shared/Clock.cs", clock + "#if DEBUG || NETFRAMEWORK\n// diagnostics\n#endif\n#if NETFRAMEWORK && DEBUG\n// framework debug\n#endif\n");
        var diagnostics = new DiagnosticBag();

        var plan = IfdefStripPlanner.Plan(Request(fixture, keep: false, diagnostics));

        var left = Assert.Single(plan.Result.NotStripped);
        Assert.Equal(("src/Shared/Clock.cs", 11, "DEBUG || NETFRAMEWORK"), (left.File, left.Line, left.Condition));
        Assert.True(diagnostics.Contains("OFR3603"));
        var after = Encoding(plan, "src/Shared/Clock.cs");
        Assert.Contains("#if DEBUG || NETFRAMEWORK\n// diagnostics\n#endif\n", after, StringComparison.Ordinal);
        Assert.DoesNotContain("framework debug", after, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3601")]
    [ProducesDiagnostic("OFR3602")]
    public async Task Wrap_takes_members_only_shared_code_does_not_need_and_skips_stale_findings()
    {
        var fixture = await ScannedFixtures.ScanAsync("netfx-only");
        using var repository = fixture.Repository;
        var diagnostics = new DiagnosticBag();
        WrapFinding[] findings =
        [
            At(repository, "OFR3001", "src/Legacy.Core/Thumbnails.cs", "public Bitmap Blank", "Bitmap", "System.Drawing.Bitmap"),
            At(repository, "OFR3001", "src/Legacy.Core/Thumbnails.cs", "return new Bitmap", "Bitmap", "System.Drawing.Bitmap"),
            At(repository, "OFR3001", "src/Legacy.Core/UrlHelper.cs", "return HttpContext.Current", "HttpContext", "System.Web.HttpContext"),
            At(repository, "OFR3001", "src/Legacy.Core/UrlHelper.cs", "return HttpUtility.UrlEncode", "UrlEncode", "System.Web.HttpUtility.UrlEncode(string)"),
            At(repository, "OFR3001", "src/Legacy.Core/UrlHelper.cs", "return HttpUtility.UrlEncode", "HttpUtility", "System.Web.HttpContext"),
        ];

        var plan = IfdefWrapPlanner.Plan(new IfdefWrapRequest
        {
            RepositoryRoot = repository.Path,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            Findings = findings,
            Diagnostics = diagnostics,
        });

        Assert.Equal(
            [("src/Legacy.Core/Thumbnails.cs", 13, 16, "member", "Blank()", 2), ("src/Legacy.Core/UrlHelper.cs", 12, 15, "member", "CurrentPath()", 1)],
            plan.Result.Wraps.Select(w => (w.File, w.StartLine, w.EndLine, w.Kind, w.Target, w.Findings)));
        Assert.Equal(["OFR3601", "OFR3602"], plan.Result.NotWrapped.Select(n => n.Code).Order(StringComparer.Ordinal));
        var shared = plan.Result.NotWrapped.Single(n => n.Code == "OFR3601");
        Assert.Contains("used at src/Legacy.App/Program.cs:", shared.Reason, StringComparison.Ordinal);
        Assert.True(diagnostics.Contains("OFR3601") && diagnostics.Contains("OFR3602"));

        var thumbnails = Encoding(plan, "src/Legacy.Core/Thumbnails.cs");
        Assert.Equal(
            Before(repository, "src/Legacy.Core/Thumbnails.cs").Replace(
                "        public Bitmap Blank(int width, int height)\n        {\n            return new Bitmap(width, height);\n        }\n",
                "#if NETFRAMEWORK\n        public Bitmap Blank(int width, int height)\n        {\n            return new Bitmap(width, height);\n        }\n#endif\n",
                StringComparison.Ordinal),
            thumbnails);
    }

    [Fact]
    public async Task Wrap_takes_the_statement_when_removing_it_keeps_the_method_valid()
    {
        var fixture = await ScannedFixtures.ScanAsync("behavior");
        using var repository = fixture.Repository;
        const string File = "src/Behavior.Legacy/Rules/Api.cs";

        var plan = IfdefWrapPlanner.Plan(new IfdefWrapRequest
        {
            RepositoryRoot = repository.Path,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            Findings =
            [
                At(repository, "OFR3002", File, "Console.Beep(800, 200);", "Beep", "System.Console.Beep(int, int)"),
                At(repository, "OFR3003", File, "work.BeginInvoke", "BeginInvoke", "System.Action.BeginInvoke(System.AsyncCallback, object)"),
                At(repository, "OFR3001", File, "return System.Web.HttpContext.Current;", "HttpContext", "System.Web.HttpContext"),
            ],
            Condition = "!NET10_0_OR_GREATER",
            Diagnostics = new DiagnosticBag(),
        });

        Assert.Equal(["member Positive()", "statement Positive(): Console.Beep(800, 200);", "statement PositiveBeginInvoke(): work.BeginInvoke(null, null);"],
            plan.Result.Wraps.Select(w => w.Kind + " " + w.Target).Order(StringComparer.Ordinal));
        var api = Encoding(plan, File);
        Assert.Contains("#if !NET10_0_OR_GREATER\n            Console.Beep(800, 200);\n#endif\n", api, StringComparison.Ordinal);
        Assert.Contains("            Action work = () => { };\n#if !NET10_0_OR_GREATER\n            work.BeginInvoke(null, null);\n#endif\n", api, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_already_inside_the_condition_are_left_alone()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;

        var plan = IfdefWrapPlanner.Plan(new IfdefWrapRequest
        {
            RepositoryRoot = repository.Path,
            Model = WorkspaceStore.Read(fixture.WorkspacePath),
            Findings = [At(repository, "OFR3001", "src/Shared/Formatter.cs", "System.Web.HttpUtility.UrlEncode", "HttpUtility", "System.Web.HttpUtility")],
            Diagnostics = new DiagnosticBag(),
        });

        Assert.Equal(1, plan.Result.AlreadyGuarded);
        Assert.Empty(plan.Result.Wraps);
        Assert.Null(plan.ChangeSet);
    }

    [Fact]
    public void Findings_are_read_from_the_document_or_the_envelope()
    {
        const string Document = """{ "audit": "api", "findings": [ { "rule": "OFR3001", "file": "a.cs", "line": 3, "column": 5, "symbol": "X" } ] }""";

        Assert.Equal([new WrapFinding("OFR3001", "a.cs", 3, 5, "X")], WrapFinding.Parse(Document));
        Assert.Equal([new WrapFinding("OFR3001", "a.cs", 3, 5, "X")], WrapFinding.Parse("""{ "offramp": {}, "result": """ + Document + " }"));
        Assert.Throws<System.Text.Json.JsonException>(() => WrapFinding.Parse("""{ "graph": [] }"""));
    }

    /// <summary>A finding at the first line containing <paramref name="line"/>, at <paramref name="token"/> on it.</summary>
    private static WrapFinding At(FixtureRepository repository, string rule, string file, string line, string token, string symbol)
    {
        var lines = repository.Directory.Read(file).Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(line, StringComparison.Ordinal));
        Assert.True(index >= 0, $"'{line}' is not in {file}");
        var column = lines[index].IndexOf(token, lines[index].IndexOf(line, StringComparison.Ordinal), StringComparison.Ordinal) + 1;
        return new WrapFinding(rule, file, index + 1, column, symbol);
    }

    private static IfdefStripRequest Request(ScannedFixture fixture, bool keep, DiagnosticBag diagnostics) => new()
    {
        RepositoryRoot = fixture.Root,
        Model = WorkspaceStore.Read(fixture.WorkspacePath),
        Symbol = "NETFRAMEWORK",
        Keep = keep,
        Diagnostics = diagnostics,
    };

    private static string Before(FixtureRepository repository, string file) => repository.Directory.Read(file).ReplaceLineEndings("\n");

    private static string Encoding(IfdefStripPlan plan, string file) => After(plan.ChangeSet!, file);

    private static string Encoding(IfdefWrapPlan plan, string file) => After(plan.ChangeSet!, file);

    private static string After(ChangeSets.ChangeSet changeSet, string file) =>
        new System.Text.UTF8Encoding(false).GetString(changeSet.Edits.Single(e => e.Path == file).After).TrimStart('﻿').ReplaceLineEndings("\n");
}
