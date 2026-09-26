using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Slicing;

namespace Offramp.Workspace.Verification;

public sealed record VerifyRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required VerifyConfig Config { get; init; }

    public required VerifyMode Mode { get; init; }

    /// <summary>Repository-relative projects to verify, sorted.</summary>
    public required IReadOnlyList<string> Projects { get; init; }

    /// <summary>Build the model's solution itself rather than a filter of <see cref="Projects"/>.</summary>
    public bool Everything { get; init; }

    /// <summary>How the projects were chosen, for the result.</summary>
    public required string Scope { get; init; }

    /// <summary>The target framework moniker, for <c>OFFRAMP_VERIFY_TARGET</c>.</summary>
    public required string TargetFramework { get; init; }

    /// <summary>A change set JSON file for <c>OFFRAMP_VERIFY_CHANGESET</c>, or null.</summary>
    public string? ChangeSetPath { get; init; }

    /// <summary>Write the current errors as the baseline instead of comparing with it.</summary>
    public bool RecordBaseline { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>
/// Runs the user's verification (docs/spec/commands/workspace.md#verify): the real
/// <c>dotnet build</c> with the configured properties, or <c>verify.command</c>,
/// then groups the errors and compares them with the recorded baseline.
/// </summary>
public static partial class VerifyRunner
{
    public const string Directory = ".offramp/verify";
    public const string BaselineFile = Directory + "/baseline.json";
    private const int TailLines = 20;

    /// <summary>The result, or null when the SDK or shell could not be started (<c>OFR0010</c>).</summary>
    public static async Task<VerifyResult?> RunAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode == VerifyMode.None || request.Projects.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR5090, request.Mode == VerifyMode.None
                ? "Verification is turned off (verify.mode: none); nothing was built."
                : $"No project is left to verify ({request.Scope}); nothing was built.");
            return new VerifyResult
            {
                Mode = request.Mode,
                Status = VerifyStatus.Skipped,
                Scope = request.Scope,
                Projects = [.. request.Projects.Select(p => new VerifyProject(p, VerifyProjectStatus.NotVerified, 0))],
                Errors = [],
                ErrorCount = 0,
                Invocations = [],
            };
        }

        System.IO.Directory.CreateDirectory(RepoPaths.ToAbsolute(request.RepositoryRoot, Directory));
        var run = request.Mode == VerifyMode.Build
            ? await BuildAsync(request, cancellationToken)
            : await CommandAsync(request, cancellationToken);
        return run is null ? null : Conclude(request, run);
    }

    /// <summary>What running the verification produced, before the baseline and the verdict.</summary>
    private sealed record Run(List<VerifyInvocation> Invocations, List<VerifyError> Errors, List<string> OutputTail)
    {
        public bool TimedOut => Invocations.Any(i => i.TimedOut);

        public bool ExitedCleanly => Invocations.All(i => i.ExitCode == 0 && !i.TimedOut);
    }

    private static async Task<Run?> BuildAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        foreach (var stale in System.IO.Directory.EnumerateFiles(RepoPaths.ToAbsolute(root, Directory), "verify*.binlog"))
        {
            File.Delete(stale);
        }

        var targets = Targets(request);
        var run = new Run([], [], []);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(request.Config.TimeoutSeconds);
        for (var i = 0; i < targets.Count; i++)
        {
            var binlog = targets.Count == 1 ? $"{Directory}/verify.binlog" : string.Create(CultureInfo.InvariantCulture, $"{Directory}/verify-{i + 1}.binlog");
            var arguments = BuildArguments(targets[i], binlog, request.Config);
            ProcessResult result;
            using (request.Progress.BeginPhase($"Building {targets[i]}", i + 1, targets.Count))
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                result = await request.Processes.RunAsync(
                    new ProcessSpec("dotnet", arguments) { WorkingDirectory = root, Timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1) },
                    cancellationToken);
            }

            if (result.NotFound)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR0010, "`dotnet` could not be started, so nothing can be verified. Install the .NET SDK.");
                return null;
            }

            run.Invocations.Add(new VerifyInvocation
            {
                CommandLine = "dotnet " + string.Join(' ', arguments.Select(Quote)),
                Binlog = File.Exists(RepoPaths.ToAbsolute(root, binlog)) ? binlog : null,
                ExitCode = result.ExitCode,
                TimedOut = result.TimedOut,
            });
            if (result.TimedOut)
            {
                break;
            }

            run.Errors.AddRange(BuildErrors(root, binlog, result));
        }

        return run;
    }

    /// <summary>The solution; a filter of it for a selection; or each project when the model has no solution.</summary>
    private static List<string> Targets(VerifyRequest request)
    {
        var solution = request.Model.Solution;
        if (request.Everything && solution is not null)
        {
            return [solution];
        }

        var underlying = solution is null ? null : UnderlyingSolution(request.RepositoryRoot, solution);
        if (underlying is null)
        {
            return [.. request.Projects];
        }

        var filter = Directory + "/verify.slnf";
        File.WriteAllText(RepoPaths.ToAbsolute(request.RepositoryRoot, filter),
            SliceBuilder.SolutionFilter(underlying, request.Projects, filter), new System.Text.UTF8Encoding(false));
        return [filter];
    }

    /// <summary>The .sln or .slnx a solution path stands for: itself, or the one a .slnf filters.</summary>
    internal static string? UnderlyingSolution(string repositoryRoot, string solution)
    {
        if (!solution.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return solution;
        }

        try
        {
            var filter = JsonNode.Parse(File.ReadAllText(RepoPaths.ToAbsolute(repositoryRoot, solution)));
            var inner = filter?["solution"]?["path"]?.GetValue<string>();
            if (inner is null)
            {
                return null;
            }

            var directory = Path.GetDirectoryName(RepoPaths.ToAbsolute(repositoryRoot, solution))!;
            return RepoPaths.ToRepositoryRelative(repositoryRoot, Path.GetFullPath(inner.Replace('\\', Path.DirectorySeparatorChar), directory));
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static List<string> BuildArguments(string target, string binlog, VerifyConfig config)
    {
        var arguments = new List<string> { "build", target, "-nologo", "-v:minimal", "-nodeReuse:false", "-bl:" + binlog, "-c", config.Configuration };
        if (!config.Restore)
        {
            arguments.Add("--no-restore");
        }

        arguments.AddRange(config.Properties.Select(p => $"-p:{p.Key}={p.Value}"));
        if (config.WarnAsError.Count > 0)
        {
            arguments.Add("-warnaserror:" + string.Join(';', config.WarnAsError));
        }

        if (config.NoWarn.Count > 0)
        {
            arguments.Add("-nowarn:" + string.Join(';', config.NoWarn));
        }

        return arguments;
    }

    private static List<VerifyError> BuildErrors(string root, string binlog, ProcessResult result)
    {
        var path = RepoPaths.ToAbsolute(root, binlog);
        if (File.Exists(path))
        {
            try
            {
                var logged = BinlogReader.Read(path).Errors.Select(e => ToError(root, e)).ToList();
                if (logged.Count > 0 || result.ExitCode == 0)
                {
                    return logged;
                }
            }
            catch (InvalidDataException)
            {
                // Unreadable log: fall back to what the build printed.
            }
        }

        // A failed build always yields at least one error, so a baseline can never excuse a crash it did not record.
        return result.ExitCode == 0 ? [] :
        [
            new VerifyError { Code = "", Message = Scrub(root, string.Join('\n', Tail(result.StandardOutput + result.StandardError))) },
        ];
    }

    private static VerifyError ToError(string root, BuildError error)
    {
        var project = error.ProjectFile is { Length: > 0 } p && Path.IsPathRooted(p) ? RepoPaths.ToRepositoryRelative(root, p) : null;
        string? file = null;
        if (error.File is { Length: > 0 } f)
        {
            var absolute = Path.IsPathRooted(f) ? f
                : error.ProjectFile is { Length: > 0 } projectFile ? Path.GetFullPath(f, Path.GetDirectoryName(projectFile)!)
                : Path.GetFullPath(f, root);
            file = RepoPaths.ToRepositoryRelative(root, absolute);
        }

        return new VerifyError
        {
            Code = error.Code,
            Message = Scrub(root, error.Message),
            Project = project,
            File = file,
            Line = error.Line,
            Column = error.Column,
        };
    }

    private static async Task<Run?> CommandAsync(VerifyRequest request, CancellationToken cancellationToken)
    {
        var command = request.Config.Command!;
        var spec = (OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/d", "/s", "/c", command])
            : new ProcessSpec("/bin/sh", ["-c", command])) with
        {
            WorkingDirectory = request.RepositoryRoot,
            Timeout = TimeSpan.FromSeconds(request.Config.TimeoutSeconds),
            Environment = new Dictionary<string, string?>
            {
                ["OFFRAMP_VERIFY_PROJECTS"] = string.Join(';', request.Projects),
                ["OFFRAMP_VERIFY_TARGET"] = request.TargetFramework,
                ["OFFRAMP_VERIFY_CHANGESET"] = request.ChangeSetPath,
            },
        };

        ProcessResult result;
        using (request.Progress.BeginPhase("Running verify.command", 1, 1))
        {
            result = await request.Processes.RunAsync(spec, cancellationToken);
        }

        if (result.NotFound)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0010, $"The shell ({spec.FileName}) could not be started to run verify.command.");
            return null;
        }

        var run = new Run(
            [new VerifyInvocation { CommandLine = command, ExitCode = result.ExitCode, TimedOut = result.TimedOut }],
            [],
            []);
        var merged = MergeEnvelope(request, result.StandardOutput);
        run.Errors.AddRange(merged ?? []);
        if (!result.Succeeded && !result.TimedOut && run.Errors.Count == 0)
        {
            run.Errors.Add(new VerifyError { Code = "", Message = string.Create(CultureInfo.InvariantCulture, $"verify.command exited with code {result.ExitCode}.") });
        }
        if (!result.Succeeded)
        {
            run.OutputTail.AddRange(Tail((merged is null ? result.StandardOutput : "") + result.StandardError).Select(l => Scrub(request.RepositoryRoot, l)));
        }

        return run;
    }

    /// <summary>
    /// When the command printed a JSON envelope, adds its diagnostics to the bag (Offramp codes as
    /// they are, other codes under <c>OFR5020</c>) and returns its errors; null when stdout is not an envelope.
    /// </summary>
    private static List<VerifyError>? MergeEnvelope(VerifyRequest request, string stdout)
    {
        var text = stdout.Trim();
        if (!text.StartsWith('{'))
        {
            return null;
        }

        JsonArray? diagnostics;
        try
        {
            diagnostics = JsonNode.Parse(text)?["diagnostics"] as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }

        if (diagnostics is null)
        {
            return null;
        }

        var errors = new List<VerifyError>();
        foreach (var node in diagnostics.OfType<JsonObject>())
        {
            var code = Text(node, "code") ?? "";
            var message = Text(node, "message") ?? "";
            var severity = Text(node, "severity") switch
            {
                "error" => Severity.Error,
                "warning" => Severity.Warning,
                _ => Severity.Info,
            };
            var location = new DiagnosticLocation(Text(node, "project"), Text(node, "file"), Number(node, "line"), Number(node, "column"));
            if (OfframpCode().IsMatch(code))
            {
                request.Diagnostics.Add(new Diagnostic
                {
                    Code = code,
                    Severity = severity,
                    Message = message,
                    Project = location.Project,
                    File = location.File,
                    Line = location.Line,
                    Column = location.Column,
                    Help = "https://offramp.dev/diagnostics/" + code,
                });
            }
            else
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR5020, code.Length == 0 ? message : $"{code}: {message}", location,
                    [KeyValuePair.Create<string, JsonNode?>("code", code), KeyValuePair.Create<string, JsonNode?>("source", "verify.command")],
                    severity);
            }

            if (severity == Severity.Error)
            {
                errors.Add(new VerifyError
                {
                    Code = code,
                    Message = message,
                    Project = location.Project,
                    File = location.File,
                    Line = location.Line,
                    Column = location.Column,
                });
            }
        }

        return errors;
    }

    private static VerifyResult Conclude(VerifyRequest request, Run run)
    {
        var root = request.RepositoryRoot;
        var errors = run.Errors
            .DistinctBy(e => (e.Code, e.Project, e.File, e.Line, e.Column, e.Message))
            .OrderBy(e => e.Project ?? "", StringComparer.Ordinal)
            .ThenBy(e => e.File ?? "", StringComparer.Ordinal)
            .ThenBy(e => e.Line ?? 0)
            .ThenBy(e => e.Column ?? 0)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .ThenBy(e => e.Message, StringComparer.Ordinal)
            .ToList();

        string? recorded = null;
        VerifyBaselineComparison? comparison = null;
        var counted = errors;
        var baselinePath = RepoPaths.ToAbsolute(root, BaselineFile);
        VerifyBaseline? baseline = null;
        if (request.RecordBaseline && !run.TimedOut)
        {
            // Recording accepts the current errors: the run is then judged against them.
            baseline = new VerifyBaseline
            {
                Errors = [.. errors.Select(Identity).Distinct()
                    .OrderBy(e => e.Code, StringComparer.Ordinal).ThenBy(e => e.Project ?? "", StringComparer.Ordinal)
                    .ThenBy(e => e.File ?? "", StringComparer.Ordinal).ThenBy(e => e.Message, StringComparer.Ordinal)],
            };
            File.WriteAllText(baselinePath, OfframpJson.Serialize(baseline, WorkspaceJsonContext.Default.VerifyBaseline), new System.Text.UTF8Encoding(false));
            recorded = BaselineFile;
        }
        else if (File.Exists(baselinePath))
        {
            baseline = JsonSerializer.Deserialize(File.ReadAllText(baselinePath), WorkspaceJsonContext.Default.VerifyBaseline)
                ?? throw new InvalidDataException($"{BaselineFile} is empty.");
        }

        if (baseline is not null)
        {
            var known = baseline.Errors.ToHashSet();
            var knownCodes = baseline.Errors.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
            counted = [.. errors.Where(e => !known.Contains(Identity(e)))];
            var current = errors.Select(Identity).ToHashSet();
            comparison = new VerifyBaselineComparison
            {
                Path = BaselineFile,
                Known = errors.Count - counted.Count,
                New = counted.Count,
                Fixed = baseline.Errors.Count(e => !current.Contains(e)),
                NewCodes = [.. counted.Select(e => e.Code).Where(c => !knownCodes.Contains(c)).Distinct().Order(StringComparer.Ordinal)],
            };
        }

        var status = Verdict(run, counted, comparison);
        Report(request, status, counted, comparison);
        return new VerifyResult
        {
            Mode = request.Mode,
            Status = status,
            Scope = request.Scope,
            Projects = Projects(request, counted, status),
            Errors = [.. counted.GroupBy(e => e.Code, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new VerifyErrorGroup(g.Key, g.Count(), g.First()))],
            ErrorCount = counted.Count,
            Baseline = comparison,
            BaselineRecorded = recorded,
            Invocations = run.Invocations,
            OutputTail = run.OutputTail,
        };
    }

    /// <summary>
    /// Passed when every process exited 0, or when a baseline accounts for every error (a failed
    /// process always contributes at least one error, so an unrecorded crash never passes).
    /// </summary>
    private static VerifyStatus Verdict(Run run, List<VerifyError> counted, VerifyBaselineComparison? comparison)
    {
        if (run.TimedOut)
        {
            return VerifyStatus.TimedOut;
        }

        if (run.ExitedCleanly)
        {
            return VerifyStatus.Passed;
        }

        return comparison is not null && counted.Count == 0 ? VerifyStatus.Passed : VerifyStatus.Failed;
    }

    private static void Report(VerifyRequest request, VerifyStatus status, List<VerifyError> counted, VerifyBaselineComparison? comparison)
    {
        if (status == VerifyStatus.TimedOut)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR5002,
                string.Create(CultureInfo.InvariantCulture, $"Verification did not finish within {request.Config.TimeoutSeconds} seconds (verify.timeoutSeconds)."),
                data: [KeyValuePair.Create<string, JsonNode?>("timeoutSeconds", request.Config.TimeoutSeconds)]);
            return;
        }

        foreach (var code in comparison?.NewCodes ?? [])
        {
            var first = counted.First(e => e.Code == code);
            request.Diagnostics.Report(DiagnosticCatalog.OFR5010, $"{Label(code)} is new: the baseline has no error with that code. First: {first.Message}",
                new DiagnosticLocation(first.Project, first.File, first.Line, first.Column),
                [KeyValuePair.Create<string, JsonNode?>("errorCode", code)]);
        }

        if (status == VerifyStatus.Failed)
        {
            var codes = counted.Select(e => e.Code).Distinct().Order(StringComparer.Ordinal).ToList();
            var projects = counted.Select(e => e.Project).Where(p => p is not null).Distinct().Count();
            var message = counted.Count == 0
                ? "Verification failed without reporting an error; see the invocation's output."
                : string.Create(CultureInfo.InvariantCulture,
                    $"Verification failed: {counted.Count} error{(counted.Count == 1 ? "" : "s")}{(comparison is null ? "" : " not in the baseline")}{(projects == 0 ? "" : $" in {projects} project{(projects == 1 ? "" : "s")}")} ({string.Join(", ", codes.Select(Label))}).");
            request.Diagnostics.Report(DiagnosticCatalog.OFR5001, message,
                data:
                [
                    KeyValuePair.Create<string, JsonNode?>("errors", counted.Count),
                    KeyValuePair.Create<string, JsonNode?>("errorCodes", new JsonArray([.. codes.Select(c => (JsonNode?)JsonValue.Create(c))])),
                ]);
        }
    }

    private static List<VerifyProject> Projects(VerifyRequest request, List<VerifyError> counted, VerifyStatus status)
    {
        var byProject = counted.Where(e => e.Project is not null).GroupBy(e => e.Project!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return
        [
            .. request.Projects.Union(byProject.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(p => new VerifyProject(
                    p,
                    byProject.ContainsKey(p) ? VerifyProjectStatus.Failed : status == VerifyStatus.Passed ? VerifyProjectStatus.Passed : VerifyProjectStatus.NotVerified,
                    byProject.GetValueOrDefault(p))),
        ];
    }

    private static VerifyBaselineError Identity(VerifyError error) => new(error.Code, error.Project, error.File, error.Message);

    /// <summary>Removes the repository root from a message so it reads the same on every machine.</summary>
    private static string Scrub(string root, string text)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return text
            .Replace(trimmed + Path.DirectorySeparatorChar, "", StringComparison.Ordinal)
            .Replace(trimmed.Replace('\\', '/') + "/", "", StringComparison.Ordinal)
            .Replace(trimmed, ".", StringComparison.Ordinal)
            .Trim();
    }

    private static IEnumerable<string> Tail(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).TakeLast(TailLines);

    private static string Label(string code) => code.Length == 0 ? "an error without a code" : code;

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    [System.Text.RegularExpressions.GeneratedRegex("^OFR[0-9]{4}$")]
    private static partial System.Text.RegularExpressions.Regex OfframpCode();

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument;
}
