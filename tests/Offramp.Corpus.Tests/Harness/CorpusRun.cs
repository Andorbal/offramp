using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Corpus.Tests.Harness;

/// <summary>What one Offramp command did: its arguments, exit code, envelope, and time.</summary>
public sealed record OfframpRun(IReadOnlyList<string> Arguments, int ExitCode, JsonNode Envelope, TimeSpan Duration)
{
    public JsonNode Result => Envelope["result"]!;

    public IReadOnlyList<string> Codes => [.. Diagnostics().Select(d => d["code"]!.GetValue<string>())];

    /// <summary>The envelope's diagnostics, all of them or those with <paramref name="code"/>.</summary>
    public IEnumerable<JsonNode> Diagnostics(string? code = null) =>
        Envelope["diagnostics"]!.AsArray().Select(d => d!).Where(d => code is null || d["code"]!.GetValue<string>() == code);

    public override string ToString() => $"offramp {string.Join(' ', Arguments)} (exit {ExitCode}, {Duration.TotalSeconds:0} s)";
}

/// <summary>The commands every codebase gets (see README.md), in the order they ran.</summary>
public sealed record CorpusSweep
{
    public required OfframpRun Doctor { get; init; }

    public required OfframpRun Scan { get; init; }

    /// <summary><c>scan --no-build</c> right after <see cref="Scan"/>; its model, and a second full scan's, matched, apart from <c>createdAt</c>.</summary>
    public required OfframpRun Rescan { get; init; }

    public required OfframpRun Graph { get; init; }

    public required OfframpRun Plan { get; init; }

    public required OfframpRun Report { get; init; }

    public required OfframpRun DepsAudit { get; init; }

    public required OfframpRun ResolveDlls { get; init; }

    public required OfframpRun Redirects { get; init; }

    public OfframpRun? AuditApi { get; init; }

    public OfframpRun? AuditBehavior { get; init; }

    public OfframpRun? DeadCode { get; init; }

    public OfframpRun? Modernize { get; init; }
}

/// <summary>
/// One codebase's corpus run: a fresh checkout at the pinned commit, the Offramp CLI built from this
/// repository run on it as a user would (<c>dotnet offramp.dll ... --json</c>), and every command's
/// output kept under <c>artifacts/corpus-output/&lt;name&gt;/</c> with a <c>summary.md</c>. See README.md.
/// </summary>
public sealed class CorpusRun : IAsyncDisposable
{
    /// <summary>The steps <see cref="SweepAsync"/> can leave out, for codebases where they do not apply.</summary>
    public static readonly IReadOnlyList<string> OptionalSteps = ["audit api", "audit behavior", "audit dead-code", "csproj modernize"];

    private readonly List<OfframpRun> _runs = [];
    private readonly string _tool;
    private readonly string _output;

    private CorpusRun(CorpusCodebase codebase, ScratchDirectory repository, string tool, string output)
    {
        Codebase = codebase;
        Repository = repository;
        _tool = tool;
        _output = output;
    }

    public CorpusCodebase Codebase { get; }

    /// <summary>The fresh checkout; deleted at the end unless <c>OFFRAMP_CORPUS_KEEP=1</c>.</summary>
    public ScratchDirectory Repository { get; }

    /// <summary>A command's limit: the job's, less the time to build Offramp and clean up.</summary>
    public TimeSpan CommandTimeout => TimeSpan.FromMinutes(Math.Max(10, Codebase.TimeoutMinutes - 20));

    /// <summary>
    /// Skips the test unless <c>OFFRAMP_CORPUS</c> selects <paramref name="name"/> (<c>1</c> or <c>all</c> selects
    /// every codebase; otherwise a comma-separated list of names), then checks the codebase out and writes
    /// an <c>offramp.yml</c> naming its solution.
    /// </summary>
    public static async Task<CorpusRun> OpenAsync(string name)
    {
        var codebase = CorpusCodebase.Get(name);
        Assert.SkipUnless(IsSelected(name, System.Environment.GetEnvironmentVariable("OFFRAMP_CORPUS")),
            $"Corpus tests are slow and need the network; set OFFRAMP_CORPUS=1 (or {name}) to run the {codebase.Title} one.");

        var tool = ToolPath();
        Assert.True(File.Exists(tool), $"{tool} does not exist: build the solution in the configuration the tests run in first.");
        var output = RepositoryFiles.Path("artifacts", "corpus-output", name);
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        Directory.CreateDirectory(output);
        var repository = await CorpusCheckout.FreshCopyAsync(codebase, TestContext.Current.CancellationToken);
        var corpus = new CorpusRun(codebase, repository, tool, output);
        repository.Write("offramp.yml", string.Create(CultureInfo.InvariantCulture,
            $"version: 1\nsolution: {codebase.Solution}\nverify:\n  timeoutSeconds: {(int)corpus.CommandTimeout.TotalSeconds}\n"));
        return corpus;
    }

    /// <summary>True when <paramref name="setting"/> (the value of <c>OFFRAMP_CORPUS</c>) selects <paramref name="name"/>.</summary>
    public static bool IsSelected(string name, string? setting)
    {
        var value = setting?.Trim() ?? "";
        return value is "1" or "all" or "true"
            || value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs one command with <c>--json --no-llm</c> and checks what every command must do on any codebase: finish
    /// within <see cref="CommandTimeout"/>, print an envelope that matches <c>schemas/v1/&lt;schema&gt;.json</c> (unless
    /// <paramref name="schema"/> is null), exit 0 or 1, and report no internal error (<c>OFR0099</c>).
    /// </summary>
    public async Task<OfframpRun> RunAsync(string? schema, params string[] arguments)
    {
        var stem = Path.Combine(_output, string.Create(CultureInfo.InvariantCulture,
            $"{_runs.Count + 1:00}-{string.Join('-', arguments.TakeWhile(a => !a.StartsWith('-')))}"));
        var clock = Stopwatch.StartNew();
        var process = await ProcessRunner.Instance.RunAsync(
            new ProcessSpec("dotnet", [_tool, .. arguments, "--json", "--no-llm"]) { WorkingDirectory = Repository.Path, Timeout = CommandTimeout },
            TestContext.Current.CancellationToken);
        clock.Stop();
        await File.WriteAllTextAsync(stem + ".json", process.StandardOutput, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(stem + ".stderr.txt", process.StandardError, TestContext.Current.CancellationToken);

        var described = $"offramp {string.Join(' ', arguments)} (exit {process.ExitCode}; output in {stem}.json)";
        Assert.False(process.TimedOut, $"{described} did not finish within {CommandTimeout}.");
        JsonNode? envelope = null;
        try
        {
            envelope = JsonNode.Parse(process.StandardOutput);
        }
        catch (JsonException)
        {
        }

        Assert.True(envelope is not null, $"{described} printed no JSON envelope. Standard error ends:\n{Tail(process.StandardError)}");
        var run = new OfframpRun(arguments, process.ExitCode, envelope!, clock.Elapsed);
        _runs.Add(run);
        Assert.False(run.Codes.Contains("OFR0099"), $"{described} failed with an internal error (OFR0099).");
        Assert.True(process.ExitCode is 0 or 1, $"{described}: exit 2 is a usage error, 3 an environment failure or crash.");
        if (schema is not null)
        {
            SchemaAssert.ValidEnvelope(process.StandardOutput, schema);
        }

        return run;
    }

    /// <summary>
    /// The standard sweep: <c>doctor --fix --apply</c>, <c>scan</c> (and <c>scan --no-build</c> and a second <c>scan</c>, whose models must match),
    /// <c>graph</c>, <c>plan</c>, <c>report</c>, <c>deps audit</c>, <c>deps resolve-dlls</c>, <c>redirects sync</c>
    /// (dry run), and the <see cref="OptionalSteps"/> not in <paramref name="skip"/>. Nothing in it writes to the
    /// codebase except <c>doctor --fix</c> (the compile-only block and the Windows conditions), scan's restore, and the build.
    /// </summary>
    public async Task<CorpusSweep> SweepAsync(params string[] skip)
    {
        Assert.All(skip, s => Assert.Contains(s, OptionalSteps));
        bool Runs(string step) => !skip.Contains(step, StringComparer.Ordinal);

        var doctor = await RunAsync("doctor", "doctor", "--fix", "--apply", "--yes");
        var scan = await RunAsync("scan", "scan");
        var model = Scrubbed(Repository.Read(".offramp/workspace.json"));
        var rescan = await RunAsync("scan", "scan", "--no-build");
        AssertSameModel(model, Scrubbed(Repository.Read(".offramp/workspace.json")), "scan --no-build");

        // A second build: the parallel build finishes its compilations in another order (SmartStoreNET P1 #8).
        await RunAsync("scan", "scan");
        AssertSameModel(model, Scrubbed(Repository.Read(".offramp/workspace.json")), "a second scan");

        return new CorpusSweep
        {
            Doctor = doctor,
            Scan = scan,
            Rescan = rescan,
            Graph = await RunAsync("graph", "graph", "--format", "json"),
            Plan = await RunAsync("plan", "plan"),
            Report = await RunAsync("report", "report", "--format", "json", "--out", Path.Combine(_output, "report.json")),
            DepsAudit = await RunAsync("deps-audit", "deps", "audit"),
            ResolveDlls = await RunAsync("deps-resolve-dlls", "deps", "resolve-dlls"),
            Redirects = await RunAsync("redirects-sync", "redirects", "sync", "--prune"),
            AuditApi = Runs("audit api") ? await RunAsync("audit", "audit", "api") : null,
            AuditBehavior = Runs("audit behavior") ? await RunAsync("audit", "audit", "behavior") : null,
            DeadCode = Runs("audit dead-code") ? await RunAsync("dead-code", "audit", "dead-code") : null,
            Modernize = Runs("csproj modernize") ? await RunAsync("csproj-modernize", "csproj", "modernize", "--all") : null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_output, "summary.md"), Summary());
        if (System.Environment.GetEnvironmentVariable("OFFRAMP_CORPUS_KEEP") == "1")
        {
            TestContext.Current.SendDiagnosticMessage($"Kept the {Codebase.Name} checkout at {Repository.Path}.");
            return;
        }

        Repository.Dispose();
    }

    /// <summary>A Markdown table of every command: exit code, time, and diagnostics by code.</summary>
    private string Summary()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# {Codebase.Title}\n\n`{Codebase.Repository}` at `{Codebase.Commit}`\n\n");
        text.Append("| # | Command | Exit | Time | Diagnostics |\n|---:|---|---:|---:|---|\n");
        for (var i = 0; i < _runs.Count; i++)
        {
            var run = _runs[i];
            var codes = string.Join(", ", run.Codes.GroupBy(c => c, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} ×{g.Count()}"));
            text.Append(CultureInfo.InvariantCulture, $"| {i + 1} | `offramp {string.Join(' ', run.Arguments)}` | {run.ExitCode} | {run.Duration.TotalSeconds:0} s | {codes} |\n");
        }

        return text.ToString();
    }

    private void AssertSameModel(string first, string second, string other)
    {
        if (first == second)
        {
            return;
        }

        File.WriteAllText(Path.Combine(_output, "model-scan.json"), first);
        File.WriteAllText(Path.Combine(_output, "model-rescan.json"), second);
        Assert.Fail($"scan is not deterministic: the model from scan and from {other} differ (both saved in {_output}).");
    }

    /// <summary>The model without <c>createdAt</c>, the one field allowed to change between two scans.</summary>
    private static string Scrubbed(string model)
    {
        var node = JsonNode.Parse(model)!.AsObject();
        node.Remove("createdAt");
        return node.ToJsonString();
    }

    private static string ToolPath()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        return RepositoryFiles.Path("src", "Offramp.Cli", "bin", configuration, "net10.0", "offramp.dll");
    }

    private static string Tail(string text) => text.Length <= 4000 ? text : "…" + text[^4000..];
}
