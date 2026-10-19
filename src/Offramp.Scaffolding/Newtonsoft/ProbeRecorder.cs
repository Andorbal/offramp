using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Newtonsoft;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Offramp.Workspace.Verification;

namespace Offramp.Scaffolding.Newtonsoft;

/// <summary>One <c>dotnet test</c> (or configured command) of one phase of a test project's probes.</summary>
public sealed record ProbeRun
{
    public required string TestProject { get; init; }

    /// <summary>The target framework run, or null when the project was not run (OFR3739, OFR3747).</summary>
    public string? Framework { get; init; }

    /// <summary><c>netfx</c> or <c>core</c>.</summary>
    public required string Runtime { get; init; }

    /// <summary><c>write</c> (serialize probes) or <c>read</c> (the probes that read what they write).</summary>
    public required string Phase { get; init; }

    /// <summary>The results folder, repository-relative (<c>.offramp/newtonsoft/runs/&lt;name&gt;</c>).</summary>
    public required string Results { get; init; }

    public required string CommandLine { get; init; }

    public int? ExitCode { get; init; }
}

public sealed record ProbeRunRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required IReadOnlyList<ProbeTarget> Targets { get; init; }

    public required IReadOnlyList<PlannedProbe> Probes { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary>Whether .NET Framework targets can run here (Windows).</summary>
    public bool RunsNetFramework { get; init; } = OperatingSystem.IsWindows();

    /// <summary>newtonsoft.upgradeFloor: releases of the target's major at or above it are not a deployment change.</summary>
    public NuGetVersion? Floor { get; init; } = NuGetVersion.Parse("13.0.1");
}

/// <summary>What a recording or a verification observed.</summary>
public sealed record ProbeRunResult(IReadOnlyList<ProbeRun> Runs, IReadOnlyList<ProbeOutcome> Outcomes, ChangeSet? Baselines, bool BuildFailed);

/// <summary>
/// Runs the generated probes (docs/spec/commands/newtonsoft.md, "Recording" and "Verifying";
/// ADR 0067): builds the test projects, runs each one's writing probes and then its reading probes
/// with Offramp's environment, and judges the result files, never the test runner's verdict.
/// </summary>
public static class ProbeRecorder
{
    public const string RunsFolder = ".offramp/newtonsoft/runs";

    /// <summary>Runs every probe <c>newtonsoft.tests.runs</c> times and turns what the runs agree on into baselines.</summary>
    public static async Task<ProbeRunResult> RecordAsync(ProbeRunRequest request, CancellationToken cancellationToken)
    {
        if (!await BuildAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new ProbeRunResult([], [], null, BuildFailed: true);
        }

        var runs = new List<ProbeRun>();
        var names = Enumerable.Range(1, Math.Max(2, request.Config.Newtonsoft.Tests.Runs)).Select(n => "record-" + n.ToString(CultureInfo.InvariantCulture)).ToList();
        foreach (var name in names)
        {
            // What cannot run is reported once, not once per pass.
            runs.AddRange(await RunAsync(request, "record", name, report: name == names[0], cancellationToken).ConfigureAwait(false));
        }

        var outcomes = new List<ProbeOutcome>();
        var changeSet = new ChangeSet();
        foreach (var target in request.Targets)
        {
            if (runs.FirstOrDefault(r => r.TestProject == target.TestProject && r.Framework is not null) is not { } ran)
            {
                continue;
            }

            var probes = request.Probes.Where(p => p.TestProject == target.TestProject).ToList();
            var recorded = new List<(PlannedProbe Probe, ProbeRecord Record)>();
            foreach (var probe in probes)
            {
                var results = names.Select(n => Read(request.RepositoryRoot, n, ran.Runtime, probe.Id)).ToList();
                if (results.Any(r => r is null))
                {
                    outcomes.Add(Outcome(probe, ran.Runtime, ProbeStatus.Missing, null, null));
                }
                else if (results.Skip(1).FirstOrDefault(r => r!.Observation != results[0]!.Observation) is { } other)
                {
                    outcomes.Add(Outcome(probe, ran.Runtime, ProbeStatus.Quarantined, results[0]!.Newtonsoft, ProbeRecord.Diff(results[0]!.Observation, other.Observation)));
                }
                else
                {
                    recorded.Add((probe, results[0]!));
                }
            }

            // Probes must run under a release the production project is deployed with: its own, or an older one an application deploys.
            var production = request.Model.Projects.Single(p => p.Id == target.Production);
            var allowed = new List<string>();
            if (NewtonsoftDeploymentScope.Compute(request.Model, NewtonsoftRules.Measured, request.Floor).TryGetValue(production.Id, out var deployment))
            {
                allowed.AddRange(deployment.DeployedReleases
                    .Select(r => NuGetVersion.TryParse(r, out var v) && v < NewtonsoftRules.Measured && v.Major < NewtonsoftRules.Measured.Major ? $"{v.Major}.{v.Minor}.0.0" : null).OfType<string>());
            }

            if (allowed.Count == 0 && ExpectedIdentity(production, target.TestProject == production.Id) is { } expectedIdentity)
            {
                allowed.Add(expectedIdentity);
            }

            var expected = allowed.Count == 0 ? null : string.Join(" or ", allowed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            var foreign = recorded.Where(r => allowed.Count > 0 && r.Record.Newtonsoft != "none" && !allowed.Contains(r.Record.Newtonsoft, StringComparer.Ordinal)).ToList();
            if (foreign.Count > 0)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3745,
                    $"{target.TestProject} ran Newtonsoft.Json {foreign[0].Record.Newtonsoft} on {ran.Framework}, but {target.Production} uses {expected}: its baselines would record another release. Nothing is written for it. (Microsoft.NET.Test.Sdk 17.x brings Newtonsoft.Json 13 to .NET targets; 18.x does not.)",
                    new DiagnosticLocation(target.TestProject));
                outcomes.AddRange(recorded.Select(r => Outcome(r.Probe, ran.Runtime, ProbeStatus.Missing, r.Record.Newtonsoft, null)));
                continue;
            }

            Baselines(request.RepositoryRoot, target, ran.Runtime, recorded, probes, outcomes, changeSet);
        }

        Report(request, outcomes);
        return new ProbeRunResult(runs, Sorted(outcomes), changeSet.IsEmpty ? null : changeSet, BuildFailed: false);
    }

    /// <summary>Runs every probe once and compares each result with its baseline.</summary>
    public static async Task<ProbeRunResult> VerifyAsync(ProbeRunRequest request, CancellationToken cancellationToken)
    {
        if (!await BuildAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new ProbeRunResult([], [], null, BuildFailed: true);
        }

        var runs = await RunAsync(request, "verify", "verify", report: true, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<ProbeOutcome>();
        foreach (var target in request.Targets)
        {
            if (runs.FirstOrDefault(r => r.TestProject == target.TestProject && r.Framework is not null) is not { } ran)
            {
                continue;
            }

            foreach (var probe in request.Probes.Where(p => p.TestProject == target.TestProject))
            {
                var now = Read(request.RepositoryRoot, "verify", ran.Runtime, probe.Id);
                var baselinePath = RepoPaths.ToAbsolute(request.RepositoryRoot, BaselinePath(target, ran.Runtime, probe.Id));
                var baseline = File.Exists(baselinePath) ? ProbeRecord.Parse(File.ReadAllText(baselinePath)) : null;
                outcomes.Add(now is null ? Outcome(probe, ran.Runtime, ProbeStatus.Missing, null, null)
                    : baseline is null ? Outcome(probe, ran.Runtime, ProbeStatus.NoBaseline, now.Newtonsoft, null)
                    : ProbeRecord.Compare(baseline, now) is var status && status == ProbeStatus.Same ? Outcome(probe, ran.Runtime, status, now.Newtonsoft, null)
                    : Outcome(probe, ran.Runtime, status, now.Newtonsoft, ProbeRecord.Diff(baseline.Observation, now.Observation)));
            }
        }

        Report(request, outcomes);
        return new ProbeRunResult(runs, Sorted(outcomes), null, BuildFailed: false);
    }

    internal static string? ExpectedIdentity(ProjectInfo production, bool isSelfTestHost)
    {
        var framework = CompilationLoader.PreferredTarget(production);
        if (isSelfTestHost && framework is not null
            && production.Resolved.TryGetValue(framework, out var resolved)
            && resolved.Packages.FirstOrDefault(p => string.Equals(p.Id, NewtonsoftReferences.PackageId, StringComparison.OrdinalIgnoreCase)) is { } package
            && NuGetVersion.TryParse(package.Version, out var resolvedVersion))
        {
            return $"{resolvedVersion.Major}.{resolvedVersion.Minor}.0.0";
        }

        return framework is not null && NewtonsoftReferences.For(production, framework) is { } reference
            && NuGetVersion.TryParse(reference.Version, out var version)
                ? $"{version.Major}.{version.Minor}.0.0"
                : null;
    }

    public static string BaselinePath(ProbeTarget target, string runtime, string probe) =>
        $"{target.Folder}/Baseline/{runtime}/{probe}.txt";

    /// <summary>Recorded results become baselines (new or changed ones written); baselines of probes that no longer exist are deleted.</summary>
    private static void Baselines(string root, ProbeTarget target, string runtime, List<(PlannedProbe Probe, ProbeRecord Record)> recorded,
        List<PlannedProbe> probes, List<ProbeOutcome> outcomes, ChangeSet changeSet)
    {
        foreach (var (probe, record) in recorded)
        {
            var path = BaselinePath(target, runtime, probe.Id);
            var text = File.ReadAllText(Path.Combine(RepoPaths.ToAbsolute(root, RunsFolder), "record-1", runtime, probe.Id + ".txt")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var absolute = RepoPaths.ToAbsolute(root, path);
            if (!File.Exists(absolute))
            {
                changeSet.Create(path, text);
                outcomes.Add(Outcome(probe, runtime, ProbeStatus.Recorded, record.Newtonsoft, null));
            }
            else if (File.ReadAllText(absolute).Replace("\r\n", "\n", StringComparison.Ordinal) != text)
            {
                changeSet.Edit(path, File.ReadAllBytes(absolute), new UTF8Encoding(false).GetBytes(text));
                outcomes.Add(Outcome(probe, runtime, ProbeStatus.Recorded, record.Newtonsoft, null));
            }
            else
            {
                outcomes.Add(Outcome(probe, runtime, ProbeStatus.Unchanged, record.Newtonsoft, null));
            }
        }

        var folder = RepoPaths.ToAbsolute(root, $"{target.Folder}/Baseline/{runtime}");
        if (Directory.Exists(folder))
        {
            var current = probes.Select(p => p.Id + ".txt").ToHashSet(StringComparer.Ordinal);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.txt").Where(f => !current.Contains(Path.GetFileName(f))).Order(StringComparer.Ordinal))
            {
                changeSet.Delete(root, RepoPaths.ToRepositoryRelative(root, stale));
            }
        }
    }

    /// <summary>Builds the test projects with <c>verify</c>'s machinery; false (with OFR3740) when the build fails.</summary>
    private static async Task<bool> BuildAsync(ProbeRunRequest request, CancellationToken cancellationToken)
    {
        var projects = request.Targets.Select(t => t.TestProject).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (projects.Count == 0)
        {
            return true;
        }

        var build = await VerifyRunner.RunAsync(new VerifyRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = request.Model,
            Config = request.Config.Verify with { Mode = "build" },
            ScanBuilder = request.Config.Scan.Builder,
            MsbuildPath = request.Config.Scan.MsbuildPath,
            Mode = VerifyMode.Build,
            Projects = projects,
            Scope = $"the {projects.Count} test project{(projects.Count == 1 ? "" : "s")} with probes",
            TargetFramework = request.Config.TargetFramework,
            Processes = request.Processes,
            Diagnostics = request.Diagnostics,
            Progress = request.Progress,
        }, cancellationToken).ConfigureAwait(false);
        if (build is { Passed: true })
        {
            return true;
        }

        request.Diagnostics.Report(DiagnosticCatalog.OFR3740,
            build is null
                ? "The test projects could not be built: the configured build tool did not start."
                : $"The test projects do not build ({build.ErrorCount} error{(build.ErrorCount == 1 ? "" : "s")}; the first: {Describe(build.Errors.FirstOrDefault()?.First)})."
                    + (build.OutputTail.Count > 0 ? "\n" + string.Join("\n", build.OutputTail.TakeLast(10)) : ""));
        return false;
    }

    /// <summary>Runs each test project's writing probes, then its reading probes, into <c>runs/&lt;name&gt;</c>.</summary>
    private static async Task<List<ProbeRun>> RunAsync(ProbeRunRequest request, string mode, string name, bool report, CancellationToken cancellationToken)
    {
        var results = Path.Combine(RepoPaths.ToAbsolute(request.RepositoryRoot, RunsFolder), name);
        if (Directory.Exists(results))
        {
            Directory.Delete(results, recursive: true);
        }

        Directory.CreateDirectory(results);
        var runs = new List<ProbeRun>();
        foreach (var group in request.Targets.GroupBy(t => t.TestProject).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var test = request.Model.Projects.Single(p => p.Id == group.Key);
            var (framework, runtime) = Framework(test, request.RunsNetFramework);
            // A legacy project runs from its output folder, where VSTest needs an adapter to find any test.
            var adapterless = request.Config.Newtonsoft.Tests.Command is null && !test.SdkStyle && !HasAdapter(request.RepositoryRoot, test);
            foreach (var phase in new[] { "write", "read" })
            {
                var classes = group.Select(t => "." + CSharp6.Identifier(t.Folder[(t.Folder.LastIndexOf('/') + 1)..]) + "." + ClassName(request.Model, t.Production) + (phase == "write" ? "SerializeProbes." : "DeserializeProbes."))
                    .Where(c => request.Probes.Any(p => p.TestProject == group.Key && p.File.EndsWith(c.Trim('.').Split('.')[^1] + ".cs", StringComparison.Ordinal)))
                    .ToList();
                if (classes.Count == 0)
                {
                    continue;
                }

                var filter = string.Join("|", classes.Select(c => "FullyQualifiedName~" + c));
                var relative = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, results);
                if (framework is null || adapterless)
                {
                    var command = $"dotnet test {Assembly(test)} --test-adapter-path {OutputFolder(test)} --filter \"{filter}\"";
                    if (report && phase == "write" && framework is null)
                    {
                        request.Diagnostics.Report(DiagnosticCatalog.OFR3739,
                            $"{test.Id} targets only .NET Framework, which runs on Windows. On Windows, run `offramp newtonsoft tests --{mode}` (or `{command}` with OFFRAMP_NEWTONSOFT_MODE={mode} and OFFRAMP_NEWTONSOFT_RESULTS set).",
                            new DiagnosticLocation(test.Id));
                    }

                    if (report && phase == "write" && adapterless)
                    {
                        var package = TestFrameworks.AdapterPackage(test, group.First().Framework);
                        request.Diagnostics.Report(DiagnosticCatalog.OFR3747,
                            $"{test.Id} has no VSTest adapter in {OutputFolder(test)} (no *.testadapter.dll), so `dotnet test` would find none of its probes; it is not run. Add the {package} package to it, or set newtonsoft.tests.command to run its tests another way.",
                            new DiagnosticLocation(test.Id), [KeyValuePair.Create<string, JsonNode?>("package", package)]);
                    }

                    runs.Add(new ProbeRun { TestProject = test.Id, Runtime = runtime, Phase = phase, Results = relative, CommandLine = command });
                    continue;
                }

                var spec = Spec(request, test, framework, filter, results, mode, group.First().Folder);
                var outcome = await request.Processes.RunAsync(spec, cancellationToken).ConfigureAwait(false);
                // Failing tests are expected while verifying (a probe that differs throws); the result files are the verdict.
                if (outcome.NotFound || outcome.TimedOut || mode == "record" && !outcome.Succeeded)
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR3740,
                        $"Running the {phase} probes of {test.Id} ({framework}) {(outcome.TimedOut ? "timed out" : outcome.NotFound ? "could not start" : $"exited with {outcome.ExitCode}")}: {spec}\n{Tail(outcome)}",
                        new DiagnosticLocation(test.Id));
                }

                runs.Add(new ProbeRun { TestProject = test.Id, Framework = framework, Runtime = runtime, Phase = phase, Results = relative, CommandLine = spec.ToString(), ExitCode = outcome.ExitCode });
            }
        }

        return runs;
    }

    private static ProcessSpec Spec(ProbeRunRequest request, ProjectInfo test, string framework, string filter, string results, string mode, string folder)
    {
        var environment = new Dictionary<string, string?>
        {
            ["OFFRAMP_NEWTONSOFT_MODE"] = mode,
            ["OFFRAMP_NEWTONSOFT_RESULTS"] = results,
            ["OFFRAMP_NEWTONSOFT_DIR"] = RepoPaths.ToAbsolute(request.RepositoryRoot, folder),
        };
        var timeout = TimeSpan.FromSeconds(request.Config.Newtonsoft.Tests.TimeoutSeconds);
        if (request.Config.Newtonsoft.Tests.Command is { } command)
        {
            var expanded = command
                .Replace("{project}", Quote(RepoPaths.ToAbsolute(request.RepositoryRoot, test.Id)), StringComparison.Ordinal)
                .Replace("{assembly}", Quote(RepoPaths.ToAbsolute(request.RepositoryRoot, Assembly(test))), StringComparison.Ordinal)
                .Replace("{framework}", framework, StringComparison.Ordinal)
                .Replace("{filter}", Quote(filter), StringComparison.Ordinal)
                .Replace("{results}", Quote(results), StringComparison.Ordinal);
            return OperatingSystem.IsWindows()
                ? new ProcessSpec("cmd.exe", ["/d", "/s", "/c", expanded]) { RawArguments = "/d /s /c \"" + expanded + "\"", WorkingDirectory = request.RepositoryRoot, Environment = environment, Timeout = timeout }
                : new ProcessSpec("/bin/sh", ["-c", expanded]) { WorkingDirectory = request.RepositoryRoot, Environment = environment, Timeout = timeout };
        }

        // `dotnet test <assembly>` hands its arguments to VSTest, not MSBuild: VSTest rejects `-nologo`.
        string[] arguments = test.SdkStyle
            ? ["test", RepoPaths.ToAbsolute(request.RepositoryRoot, test.Id), "--no-build", "-c", request.Config.Verify.Configuration, "-f", framework, "--filter", filter, "-nologo"]
            : ["test", RepoPaths.ToAbsolute(request.RepositoryRoot, Assembly(test)), "--test-adapter-path", RepoPaths.ToAbsolute(request.RepositoryRoot, test.OutputPath ?? "."), "--filter", filter];
        return new ProcessSpec("dotnet", arguments) { WorkingDirectory = request.RepositoryRoot, Environment = environment, Timeout = timeout };
    }

    /// <summary>
    /// The target framework to run: the project's first modern .NET target (results under <c>core</c>),
    /// else its .NET Framework target where that can run (<c>netfx</c>); null when it cannot (OFR3739).
    /// </summary>
    private static (string? Framework, string Runtime) Framework(ProjectInfo test, bool netFramework)
    {
        var modern = test.TargetFrameworks.FirstOrDefault(t => NuGetFramework.Parse(t).Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp);
        if (modern is not null)
        {
            return (modern, "core");
        }

        var framework = test.TargetFrameworks.FirstOrDefault() ?? "net48";
        return (netFramework ? framework : null, "netfx");
    }

    private static string Assembly(ProjectInfo test) => OutputFolder(test) + "/" + (test.AssemblyName ?? test.Name) + ".dll";

    private static string OutputFolder(ProjectInfo test) => test.OutputPath ?? Path.GetDirectoryName(test.Id)!.Replace('\\', '/') + "/bin/Debug";

    /// <summary>Whether the output folder has a VSTest adapter: VSTest loads extensions named <c>*.TestAdapter.dll</c>.</summary>
    private static bool HasAdapter(string root, ProjectInfo test)
    {
        var folder = RepoPaths.ToAbsolute(root, OutputFolder(test));
        return Directory.Exists(folder)
            && Directory.EnumerateFiles(folder, "*.dll").Any(f => f.EndsWith(".testadapter.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static string ClassName(WorkspaceModel model, string production) =>
        CSharp6.Identifier(model.Projects.Single(p => p.Id == production).Name.Replace(".", "", StringComparison.Ordinal));

    private static ProbeRecord? Read(string root, string run, string runtime, string probe)
    {
        var path = Path.Combine(RepoPaths.ToAbsolute(root, RunsFolder), run, runtime, probe + ".txt");
        return File.Exists(path) ? ProbeRecord.Parse(File.ReadAllText(path)) : null;
    }

    private static ProbeOutcome Outcome(PlannedProbe probe, string runtime, ProbeStatus status, string? newtonsoft, string? diff) =>
        new() { Probe = probe.Id, TestProject = probe.TestProject, Runtime = runtime, Status = status, Newtonsoft = newtonsoft, Diff = diff };

    private static List<ProbeOutcome> Sorted(List<ProbeOutcome> outcomes) =>
        [.. outcomes.OrderBy(o => o.TestProject, StringComparer.Ordinal).ThenBy(o => o.Runtime, StringComparer.Ordinal).ThenBy(o => o.Probe, StringComparer.Ordinal)];

    /// <summary>One diagnostic per test project and kind of outcome, at the first probe, with the probes listed.</summary>
    internal static void Report(ProbeRunRequest request, List<ProbeOutcome> outcomes)
    {
        foreach (var group in outcomes.GroupBy(o => (o.TestProject, o.Status)).OrderBy(g => g.Key.TestProject, StringComparer.Ordinal).ThenBy(g => g.Key.Status))
        {
            var (descriptor, what) = group.Key.Status switch
            {
                ProbeStatus.Quarantined => (DiagnosticCatalog.OFR3738, "differs between runs and is quarantined (no baseline)"),
                ProbeStatus.Missing => (DiagnosticCatalog.OFR3741, "wrote no result"),
                ProbeStatus.NoBaseline => (DiagnosticCatalog.OFR3744, "has no baseline; record one with `newtonsoft tests --record --apply`"),
                ProbeStatus.Differs or ProbeStatus.NowThrows or ProbeStatus.NoLongerThrows => (DiagnosticCatalog.OFR3762, "observes something other than its baseline"),
                ProbeStatus.MessageOnly => (DiagnosticCatalog.OFR3763, "throws the same exception with another message"),
                _ => (null, null),
            };
            if (descriptor is null)
            {
                continue;
            }

            var first = group.First();
            var count = group.Count();
            request.Diagnostics.Report(descriptor,
                $"Probe {first.Probe} ({first.Status.ToString().ToLowerInvariant()}) {what}" + (count == 1 ? "." : $"; {count} probes in {group.Key.TestProject}, the first is shown.") + (first.Diff is null ? "" : "\n" + first.Diff),
                new DiagnosticLocation(group.Key.TestProject),
                [KeyValuePair.Create<string, JsonNode?>("probes", new JsonArray([.. group.Select(o => (JsonNode?)o.Probe)]))]);
        }
    }

    /// <summary>A build error as one line: code, file, and message.</summary>
    internal static string? Describe(VerifyError? error) =>
        error is null ? null : $"{error.Code} {(error.File is null ? "" : error.File + ": ")}{error.Message}";

    private static string Tail(ProcessResult outcome) =>
        string.Join("\n", (outcome.StandardOutput + outcome.StandardError).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(10));

    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
