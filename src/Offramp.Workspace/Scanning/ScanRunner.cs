using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Output;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Workspace.Environment;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Init;
using Offramp.Workspace.Model;
using Offramp.Workspace.Restore;
using Offramp.Workspace.Store;
using Offramp.Workspace.Verification;

namespace Offramp.Workspace.Scanning;

/// <summary>
/// <c>offramp scan</c>: obtain a binary log (by building, or from the user), convert
/// it to a compiler log, and write the workspace model and a ledger snapshot.
/// See docs/spec/02-workspace-model.md and docs/spec/commands/workspace.md#scan.
/// </summary>
public static class ScanRunner
{
    public const string BinlogFileName = "msbuild.binlog";
    public const string ComplogFileName = "build.complog";

    /// <summary>The solution filter <c>scan</c> builds when the solution lists ASP.NET Web Site projects.</summary>
    public const string WebSiteFilterFileName = "scan.slnf";

    public static async Task<ScanOutcome> RunAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var state = WorkspaceStore.StateDirectory(root, request.Config);

        if (request.IfStale && File.Exists(request.WorkspacePath))
        {
            var existing = WorkspaceStore.Read(request.WorkspacePath);
            if (!WorkspaceInputs.Compare(existing, root, state).IsStale)
            {
                return new ScanOutcome(Summarize(existing, request, ledger: null, upToDate: true, buildSucceeded: null, notLoaded: []), existing, ScanFailure.None);
            }
        }

        var hasComplogOnly = request.ComplogPath is not null && request.BinlogPath is null;
        var plan = hasComplogOnly ? 3 : request.BinlogPath is null && !request.NoBuild ? 5 : 4;
        var phase = 0;

        foreach (var supplied in new[] { request.BinlogPath, request.ComplogPath })
        {
            if (supplied is not null && !File.Exists(supplied))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR0004, $"'{supplied}' does not exist.",
                    data: [KeyValuePair.Create<string, JsonNode?>("path", supplied)]);
                return new ScanOutcome(null, null, ScanFailure.Environment);
            }
        }

        string? solution = request.Config.Solution;
        string binlog;
        WorkspaceSourceKind kind;
        bool? buildSucceeded = null;
        if (hasComplogOnly)
        {
            return await ScanComplogOnlyAsync(request, state, cancellationToken);
        }

        if (request.BinlogPath is not null)
        {
            binlog = request.BinlogPath;
            kind = WorkspaceSourceKind.Binlog;
        }
        else if (request.NoBuild)
        {
            binlog = Path.Combine(state, BinlogFileName);
            kind = WorkspaceSourceKind.Build;
            if (!File.Exists(binlog))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR0003,
                    $"{RepoPaths.ToRepositoryRelative(root, binlog)} does not exist; run `offramp scan` without --no-build.");
                return new ScanOutcome(null, null, ScanFailure.Environment);
            }
        }
        else
        {
            solution ??= await ResolveSolutionAsync(request, cancellationToken);
            if (solution is null)
            {
                return new ScanOutcome(null, null, request.Diagnostics.Contains("OFR0020") ? ScanFailure.Usage : ScanFailure.Environment);
            }

            binlog = Path.Combine(state, BinlogFileName);
            kind = WorkspaceSourceKind.Build;
            var builder = UsesMsbuild(request.Config) ? " with MSBuild" : "";
            var listed = await ListProjectsAsync(request, solution, cancellationToken);
            if (!OperatingSystem.IsWindows() && listed is not null && PackagesConfigRestorer.HasPackagesConfig(listed.ProjectPaths))
            {
                plan++;
                using var restoring = request.Progress.BeginPhase("Restoring packages.config packages", ++phase, plan);
                await RestorePackagesConfigAsync(request, listed, restoring, cancellationToken);
            }

            using (var building = request.Progress.BeginPhase($"Building {solution}{builder}", ++phase, plan))
            {
                var progress = new BuildProgress(building, listed?.ProjectPaths.Count ?? 0);
                var built = await BuildAsync(request, await BuildTargetAsync(request, solution, state, cancellationToken), binlog, progress.OnLine, cancellationToken);
                if (built is null)
                {
                    return new ScanOutcome(null, null, ScanFailure.Environment);
                }
            }
        }

        BinlogData data;
        using (request.Progress.BeginPhase("Reading the binary log", ++phase, plan))
        {
            try
            {
                data = BinlogReader.Read(binlog);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR0004,
                    $"'{Display(root, binlog)}' is not a readable MSBuild binary log: {ex.Message}");
                return new ScanOutcome(null, null, ScanFailure.Environment);
            }
        }

        buildSucceeded = data.Succeeded;
        var mapper = CapturePathMapper.Infer(root, data.Evaluations.Select(e => e.ProjectFile));
        solution ??= mapper.ToRelative(data.SolutionPath);
        ReportBuildErrors(request, data, mapper);

        var complog = Path.Combine(state, ComplogFileName);
        IReadOnlyList<CompilerCallInfo> calls;
        using (request.Progress.BeginPhase("Creating the compiler log", ++phase, plan))
        {
            calls = PrepareCompilerLog(request, binlog, complog, mapper);
        }

        WorkspaceModel model;
        IReadOnlyList<NotLoadedProject> notLoaded;
        using (request.Progress.BeginPhase("Building the workspace model", ++phase, plan))
        {
            // Compiler-log paths get their own mapping: Basic.CompilerLog rewrites the paths of a
            // log from another OS (D:\a\repo on Linux reads as /code/a/repo).
            var callMapper = calls.Count == 0 ? mapper : CapturePathMapper.Infer(root, calls.Select(c => c.ProjectFile));
            var callMap = MapCalls(calls, callMapper, RepoPaths.ToRepositoryRelative(root, complog));
            // Keyed like the calls: "" for a legacy project's call, which records no target framework.
            var defines = calls
                .Where(c => callMapper.ToRelative(c.ProjectFile) is not null)
                .GroupBy(c => (callMapper.ToRelative(c.ProjectFile)!, c.TargetFramework ?? ""))
                .ToDictionary(g => g.Key, g => g.First().Defines);
            // A log Offramp built differs with every build of the same inputs; only a supplied one is an input.
            var source = new WorkspaceSource(kind, Display(root, binlog), kind == WorkspaceSourceKind.Build ? null : ContentHash.Sha256File(binlog))
            {
                Complog = request.ComplogPath is null ? null : new LogFile(Display(root, request.ComplogPath), ContentHash.Sha256File(request.ComplogPath)),
            };
            (model, notLoaded) = await BuildModelAsync(request, data, mapper, callMap, defines, source, solution, state, cancellationToken);
        }

        // A failed build gives a partial model, which would put a false step in report's trend.
        string? ledgerPath;
        using (request.Progress.BeginPhase("Writing the model and ledger snapshot", ++phase, plan))
        {
            WorkspaceStore.Save(request.WorkspacePath, model);
            ledgerPath = buildSucceeded == false ? null : Ledger.Write(Ledger.Snapshot(model), Path.GetFullPath(request.Config.Report.Ledger, root), root);
        }

        return new ScanOutcome(Summarize(model, request, ledgerPath, upToDate: false, buildSucceeded, notLoaded), model, ScanFailure.None);
    }

    /// <summary>The solution to build when none is configured (docs/decisions/0050-choose-among-several-solutions.md).</summary>
    private static async Task<string?> ResolveSolutionAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        var candidates = InitPlanner.FindSolutions(request.RepositoryRoot);
        var choice = await SolutionChooser.ChooseAsync(request.RepositoryRoot, candidates, cancellationToken);
        if (choice.Solution is null && candidates.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0022,
                "No .sln or .slnx file in the repository. Pass --solution, or scan a log with --binlog.");
        }
        else if (choice.Solution is null && !candidates.Any(SolutionChooser.IsSolutionFile))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0020,
                $"Found {candidates.Count} solution filter(s) ({string.Join(", ", candidates.Take(5))}) and no solution; pass --solution or set solution: in offramp.yml.",
                data: [KeyValuePair.Create<string, JsonNode?>("candidates", new JsonArray([.. candidates.Select(c => (JsonNode?)c)]))]);
        }
        else
        {
            InitPlanner.ReportSolutionChoice(request.Diagnostics, choice, candidates, tieSeverity: null, "pass --solution or set solution: in offramp.yml.");
        }

        return choice.Solution;
    }

    /// <summary>The solution's projects, or null when it cannot be read (the build reports that).</summary>
    private static async Task<SolutionProjects?> ListProjectsAsync(ScanRequest request, string solution, CancellationToken cancellationToken)
    {
        try
        {
            return await SolutionReader.ReadAsync(RepoPaths.ToAbsolute(request.RepositoryRoot, solution), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Outside Windows, fills the packages folder from the solution's packages.config files, as
    /// <c>nuget restore</c> does on Windows: nothing else will, and every HintPath into it would dangle
    /// (<c>docs/decisions/0037-legacy-projects-outside-windows.md</c>). Reports each package on <paramref name="progress"/>.
    /// </summary>
    private static async Task RestorePackagesConfigAsync(ScanRequest request, SolutionProjects listed, IProgressPhase progress, CancellationToken cancellationToken)
    {
        var result = await PackagesConfigRestorer.RestoreAsync(listed.SolutionFile, listed.ProjectPaths, progress, cancellationToken);
        if (result is null)
        {
            return;
        }

        var folder = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, result.PackagesFolder);
        if (result.Restored.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0106,
                string.Create(CultureInfo.InvariantCulture, $"Restored {result.Restored.Count} packages.config package(s) into {folder}/, as nuget restore does on Windows."),
                data: [
                    KeyValuePair.Create<string, JsonNode?>("folder", folder),
                    KeyValuePair.Create<string, JsonNode?>("packages", new JsonArray([.. result.Restored.Select(p => (JsonNode?)p)])),
                ]);
        }

        foreach (var failure in result.Failed)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0105,
                $"{failure.Id} {failure.Version} from packages.config could not be restored into {folder}/: {failure.Reason}",
                data: [
                    KeyValuePair.Create<string, JsonNode?>("package", failure.Id),
                    KeyValuePair.Create<string, JsonNode?>("version", failure.Version),
                ]);
        }
    }

    private static bool UsesMsbuild(OfframpConfig config) => config.Scan.Builder == ScanConfig.Msbuild;

    /// <summary>
    /// What <c>dotnet build</c> builds: the solution, or, when it lists ASP.NET Web Site projects, a filter of every
    /// other project in the state folder. .NET's MSBuild has no <c>AspNetCompiler</c>, and MSB4249 stops the whole
    /// solution before any project builds (docs/decisions/0048-web-sites-bcl-build-and-mstest-v1-outside-windows.md).
    /// </summary>
    private static async Task<string> BuildTargetAsync(ScanRequest request, string solution, string state, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        if (UsesMsbuild(request.Config) || await ReadSolutionAsync(root, solution, cancellationToken) is not { WebSites.Count: > 0 } listed)
        {
            return solution;
        }

        var webSites = listed.WebSites.ToHashSet(StringComparer.Ordinal);
        var filter = RepoPaths.ToRepositoryRelative(root, Path.Combine(state, WebSiteFilterFileName));
        var projects = listed.ProjectPaths.Where(p => !webSites.Contains(p)).Select(p => RepoPaths.ToRepositoryRelative(root, p)).ToList();
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(RepoPaths.ToAbsolute(root, filter),
            Slicing.SliceBuilder.SolutionFilter(RepoPaths.ToRepositoryRelative(root, listed.SolutionFile), projects, filter), cancellationToken);
        request.Progress.Log(ProgressLevel.Info,
            string.Create(CultureInfo.InvariantCulture, $"Building {filter}: {solution} without its {webSites.Count} ASP.NET Web Site project(s), which only .NET Framework's MSBuild builds."));
        return filter;
    }

    private static async Task<ProcessResult?> BuildAsync(ScanRequest request, string solution, string binlog, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        string? msbuild = null;
        if (UsesMsbuild(request.Config))
        {
            var location = await MsbuildLocator.LocateAsync(
                request.Config.Scan.MsbuildPath, request.RepositoryRoot, request.Environment, request.Processes, cancellationToken);
            if (location.Path is null)
            {
                ReportMsbuildNotFound(request, $"MSBuild was not found: {location.NotFound}");
                return null;
            }

            msbuild = location.Path;
            request.Progress.Log(ProgressLevel.Info, $"Building with {msbuild} ({Describe(location.Source)}).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(binlog)!);
        var result = await request.Processes.RunAsync(BuildCommand(request, msbuild, solution, binlog) with { OnOutputLine = onOutputLine }, cancellationToken);

        if (result.NotFound && msbuild is not null)
        {
            ReportMsbuildNotFound(request, $"'{msbuild}' could not be started, so the solution cannot be built.");
            return null;
        }

        if (result.NotFound)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0010, "`dotnet` could not be started, so the solution cannot be built. Install the .NET SDK, or scan a log with --binlog.");
            return null;
        }

        if (result.TimedOut)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0131,
                string.Create(CultureInfo.InvariantCulture, $"The build of {solution} did not finish within {request.Config.Verify.TimeoutSeconds} seconds."));
            return null;
        }

        if (!File.Exists(binlog))
        {
            var detail = result.StandardError.Trim().Length > 0 ? result.StandardError.Trim() : result.StandardOutput.Trim();
            request.Diagnostics.Report(DiagnosticCatalog.OFR0130,
                $"The build of {solution} produced no binary log: {Scrub(FirstLines(detail, 3), CapturePathMapper.Local(request.RepositoryRoot))}");
            return null;
        }

        return result;
    }

    /// <summary>
    /// The analysis build: <c>dotnet build</c>, or MSBuild.exe when <paramref name="msbuild"/> is set, with
    /// the same meaning either way. It is never incremental (an up-to-date project skips the compiler, and
    /// the model needs every project's compiler call) and it restores, packages.config projects included
    /// under MSBuild, as Visual Studio does.
    /// </summary>
    private static ProcessSpec BuildCommand(ScanRequest request, string? msbuild, string solution, string binlog)
    {
        var solutionPath = RepoPaths.ToAbsolute(request.RepositoryRoot, solution);
        var configuration = request.Config.Verify.Configuration;
        var arguments = msbuild is null
            ? new List<string>
            {
                "build", solutionPath,
                "-bl:" + binlog, "-c", configuration,
                "-nologo", "-v:minimal", "-clp:NoSummary", "-nodeReuse:false",
                "--no-incremental",
            }
            : new List<string>
            {
                solutionPath, "-restore", "-t:Rebuild", "-m",
                "-bl:" + binlog, "-p:Configuration=" + configuration,
                "-nologo", "-v:minimal", "-clp:NoSummary", "-nodeReuse:false",
                "-p:RestorePackagesConfig=true",
            };

        // After Offramp's own properties, so verify.properties can override them. MSBuild.exe runs a
        // legacy .nuget/NuGet.targets as Visual Studio does, so only dotnet build turns it off.
        arguments.AddRange(msbuild is null
            ? BuildProperties.Arguments(request.Config.Verify)
            : request.Config.Verify.Properties.Select(p => $"-p:{p.Key}={p.Value}"));

        return new ProcessSpec(msbuild ?? "dotnet", arguments)
        {
            WorkingDirectory = request.RepositoryRoot,
            Timeout = TimeSpan.FromSeconds(request.Config.Verify.TimeoutSeconds),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        };
    }

    private static void ReportMsbuildNotFound(ScanRequest request, string message) =>
        request.Diagnostics.Report(DiagnosticCatalog.OFR0017,
            message + " Install Visual Studio or the Build Tools with MSBuild, or pass --msbuild-path (MSBuild.exe or the installation folder).");

    private static string Describe(MsbuildSource? source) => source switch
    {
        MsbuildSource.Configured => "configured",
        MsbuildSource.DeveloperPrompt => "the Developer Command Prompt's installation",
        _ => "found by vswhere",
    };

    internal static void ReportBuildErrors(ScanRequest request, BinlogData data, CapturePathMapper mapper)
    {
        if (data.Succeeded && data.Errors.Count == 0)
        {
            return;
        }

        var (message, summary) = BuildFailures.Summarize(data.Errors, mapper.ToRelative, text => Scrub(text, mapper));
        request.Diagnostics.Report(DiagnosticCatalog.OFR0130, message, data: summary);
    }

    private static IReadOnlyList<CompilerCallInfo> PrepareCompilerLog(ScanRequest request, string binlog, string complog, CapturePathMapper mapper)
    {
        if (request.ComplogPath is not null)
        {
            if (!string.Equals(Path.GetFullPath(request.ComplogPath), Path.GetFullPath(complog), StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(complog)!);
                File.Copy(request.ComplogPath, complog, overwrite: true);
            }
        }
        else if (!IsLocalCapture(mapper, request.RepositoryRoot))
        {
            // A binary log only converts where it was built: the compiler log embeds the
            // sources and references at the paths the log recorded.
            request.Diagnostics.Report(DiagnosticCatalog.OFR0132,
                "The binary log was captured in another location, so it cannot be converted to a compiler log here. Create the compiler log where the log was built (`complog create`) and pass it with --complog.");
            if (File.Exists(complog))
            {
                File.Delete(complog);
            }

            return [];
        }
        else
        {
            ReportConversionProblems(request, CompilerLogIngest.Convert(binlog, complog), mapper);

            if (!File.Exists(complog))
            {
                return [];
            }
        }

        return CompilerLogIngest.ReadCalls(complog);
    }

    /// <summary><c>OFR0132</c> for problems converting the binary log, with the build's paths made repository-relative.</summary>
    internal static void ReportConversionProblems(ScanRequest request, ConversionReport report, CapturePathMapper mapper)
    {
        if (report.Problems.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0132,
                $"{report.Problems.Count} problem(s) converting the build log; affected projects have no compiler call. First: {Scrub(report.Problems[0], mapper)}",
                data: [KeyValuePair.Create<string, JsonNode?>("problems", new JsonArray([.. report.Problems.Take(20).Select(p => (JsonNode?)Scrub(p, mapper))]))]);
        }
    }

    private static bool IsLocalCapture(CapturePathMapper mapper, string repositoryRoot) =>
        string.Equals(mapper.CaptureRoot, repositoryRoot.Replace('\\', '/').TrimEnd('/'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// (project, target framework) → the call's reference: named by project and target framework, never by its
    /// position in the log, which follows the order the build finished its compilations
    /// (docs/decisions/0049-what-the-workspace-model-records.md).
    /// </summary>
    internal static Dictionary<(string Project, string Tfm), CompilerCallRef> MapCalls(
        IReadOnlyList<CompilerCallInfo> calls, CapturePathMapper mapper, string complogRelative)
    {
        var map = new Dictionary<(string, string), CompilerCallRef>();
        foreach (var call in calls)
        {
            var project = mapper.ToRelative(call.ProjectFile);
            if (project is null)
            {
                continue;
            }

            var tfm = call.TargetFramework ?? "";
            map.TryAdd((project, tfm), new CompilerCallRef(complogRelative, project, call.TargetFramework));
        }

        return map;
    }

    private static async Task<(WorkspaceModel Model, IReadOnlyList<NotLoadedProject> NotLoaded)> BuildModelAsync(
        ScanRequest request,
        BinlogData data,
        CapturePathMapper mapper,
        IReadOnlyDictionary<(string, string), CompilerCallRef> calls,
        IReadOnlyDictionary<(string, string), IReadOnlyList<string>> defines,
        WorkspaceSource source,
        string? solution,
        string state,
        CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var listed = await ReadSolutionAsync(root, solution, cancellationToken);
        var files = CheckProjectFiles(root, data, mapper, listed);
        var context = new ProjectBuildContext
        {
            Paths = mapper,
            Config = request.Config,
            CompilerCalls = calls,
            CompilerDefines = defines,
            Excluded = new PathGlobs(request.Config.Paths.Exclude),
            Errors = data.Errors,
            FailedCompilations = data.FailedCompilations
                .Select(f => (Project: mapper.ToRelative(f.ProjectFile), Tfm: f.TargetFramework ?? ""))
                .Where(f => f.Project is not null)
                .Select(f => (f.Project!, f.Tfm))
                .ToHashSet(),
            BuildSteps = files.ToDictionary(f => f.Key, f => new BuildStepContext { Files = f.Value }, StringComparer.Ordinal),
        };
        var steps = new BuildStepContext { Executables = Executables(data, mapper), ToLocal = mapper.ToLocal, Display = text => Scrub(text, mapper) };
        BuildStepContext StepContext(string project) => files.TryGetValue(project, out var found) ? steps with { Files = found } : steps;

        var projects = new List<ProjectInfo>();
        var others = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var group in data.Evaluations.GroupBy(e => e.ProjectFile, StringComparer.Ordinal))
        {
            var id = mapper.ToRelative(group.Key);
            if (id is null)
            {
                continue;
            }

            if (!OtherProjects.IsDotNet(id))
            {
                others.Add(id);
                continue;
            }

            projects.Add(ProjectModelBuilder.Build(id, [.. group], context));
        }

        projects.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var notLoaded = FindNotLoaded(request, data, mapper, projects, listed);

        // Projects that are not C#, Visual Basic, or F# are named once, evaluated or not, and never loaded.
        others.UnionWith(notLoaded.Select(n => n.Project).Where(p => others.Contains(p) || OtherProjects.IsOtherListed(p)));
        notLoaded = [.. notLoaded.Where(n => !others.Contains(n.Project))];
        var webSites = WebSites(root, listed);
        var loading = new List<Diagnostic>();
        foreach (var other in others)
        {
            loading.Add(OtherProjects.Report(request.Diagnostics, root, other)!);
        }

        foreach (var missing in notLoaded)
        {
            loading.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0101, $"Not loaded: {missing.Reason}.",
                new DiagnosticLocation(Project: missing.Project),
                [KeyValuePair.Create<string, JsonNode?>("reason", missing.Reason)])!);
            if (missing.Project.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase))
            {
                loading.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0114,
                    "Needs Windows to build: SQL Server Database Project (.sqlproj).",
                    new DiagnosticLocation(Project: missing.Project),
                    [KeyValuePair.Create<string, JsonNode?>("step", "ssdt")])!);
                continue;
            }

            if (webSites.Contains(missing.Project))
            {
                loading.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0126,
                    "Needs Windows to build: ASP.NET Web Site project (AspNetCompiler); `dotnet build` stops the whole solution on it (MSB4249), so scan builds the solution without it.",
                    new DiagnosticLocation(Project: missing.Project),
                    [KeyValuePair.Create<string, JsonNode?>("step", "web-site")])!);
                continue;
            }

            // Without an evaluation, the evaluation error and the project's own files are the evidence.
            BuildError[] error = EvaluationError(data, mapper, missing.Project) is { } first ? [first] : [];
            foreach (var step in WindowsOnlyBuildSteps.Detect(missing.Project, [], error, StepContext(missing.Project)))
            {
                loading.Add(ReportStep(request, step, missing.Project, mapper));
            }
        }

        loading.AddRange(await ReportMissingSourcesAsync(request, files, cancellationToken));
        loading.AddRange(ReportCompileOnlyGaps(request, data, mapper, projects));

        foreach (var project in projects)
        {
            if (project.Kind == ProjectKind.Unknown)
            {
                loading.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0102,
                    $"No kind rule matched ({project.KindEvidence}); set the kind in offramp.yml.",
                    new DiagnosticLocation(Project: project.Id))!);
            }

            var evaluations = data.Evaluations.Where(e => mapper.ToRelative(e.ProjectFile) == project.Id).ToList();
            var assetsFile = evaluations.Select(e => e.Property("ProjectAssetsFile")).FirstOrDefault(p => p is not null);
            var localAssets = mapper.ToLocal(assetsFile);
            if ((project.SdkStyle || project.PackageReferences.Count > 0) && localAssets is not null && !File.Exists(localAssets))
            {
                var relativeAssets = mapper.ToRelative(assetsFile)!;
                loading.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0104,
                    $"{relativeAssets} does not exist; restore the solution to include resolved packages.",
                    new DiagnosticLocation(Project: project.Id),
                    [KeyValuePair.Create<string, JsonNode?>("assetsFile", relativeAssets)])!);
            }

            var errors = data.Errors.Where(e => e.ProjectFile is not null && string.Equals(mapper.ToRelative(e.ProjectFile), project.Id, StringComparison.OrdinalIgnoreCase));
            foreach (var step in WindowsOnlyBuildSteps.Detect(project.Id, evaluations, errors, StepContext(project.Id)))
            {
                loading.Add(ReportStep(request, step, project.Id, mapper));
            }
        }

        var graph = GraphBuilder.Build(projects);
        foreach (var cycle in graph.Cycles)
        {
            var path = GraphBuilder.CyclePath(cycle, graph.Edges);
            loading.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0120,
                $"Project reference cycle: {string.Join(" → ", path)}.",
                new DiagnosticLocation(Project: cycle[0]),
                [KeyValuePair.Create<string, JsonNode?>("path", new JsonArray([.. path.Select(p => (JsonNode?)p)]))])!);
        }

        loading.AddRange(FrameworkOnlyReferences(request.Diagnostics, projects, graph));

        var model = new WorkspaceModel
        {
            CreatedAt = EnvelopeHeader.FormatTimestamp(request.Time.GetUtcNow()),
            RepositoryRoot = root,
            Solution = solution,
            Source = source,
            Sdk = new SdkInfo(data.SdkVersion ?? "unknown", data.RuntimeIdentifier ?? "unknown"),
            Projects = projects,
            Graph = graph,
            Packages = PackageIndex(projects),
            Inputs = WorkspaceInputs.Collect(root, state, solution, ImportedFiles(data, mapper)),
            Diagnostics = [.. loading.Where(d => d is not null).OrderBy(d => d, DiagnosticOrder.Instance)],
        };
        return (model, notLoaded);
    }

    /// <summary>The files the evaluations imported from inside the repository, repository-relative (they shape the model too).</summary>
    private static IEnumerable<string> ImportedFiles(BinlogData data, CapturePathMapper mapper) =>
        data.Evaluations.SelectMany(e => e.Imports).Distinct(StringComparer.Ordinal).Select(mapper.ToRelative).OfType<string>();

    /// <summary>The projects the scanned solution lists, or null without a readable one.</summary>
    private static async Task<SolutionProjects?> ReadSolutionAsync(string root, string? solution, CancellationToken cancellationToken)
    {
        var solutionPath = solution is null ? null : RepoPaths.ToAbsolute(root, solution);
        if (solutionPath is null || !File.Exists(solutionPath))
        {
            return null;
        }

        try
        {
            return await SolutionReader.ReadAsync(solutionPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // The build reports an unreadable solution.
        }
    }

    /// <summary>
    /// The static checks of each project's files (docs/decisions/0047-static-checks-of-project-files.md): the
    /// projects MSBuild evaluated and those the solution lists that it did not. Only for a log built in this
    /// checkout, where the files are the ones the build saw.
    /// </summary>
    private static Dictionary<string, ProjectFileFindings> CheckProjectFiles(string root, BinlogData data, CapturePathMapper mapper, SolutionProjects? listed)
    {
        var result = new Dictionary<string, ProjectFileFindings>(StringComparer.Ordinal);
        if (!IsLocalCapture(mapper, root))
        {
            return result;
        }

        var evaluations = data.Evaluations
            .Select(e => (Id: mapper.ToRelative(e.ProjectFile), Evaluation: e))
            .Where(e => e.Id is not null)
            .GroupBy(e => e.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Evaluation).ToList(), StringComparer.Ordinal);
        var solutionDirectory = listed is null ? null : Path.GetDirectoryName(listed.SolutionFile);
        var ids = evaluations.Keys
            .Concat((listed?.ProjectPaths ?? []).Select(p => RepoPaths.ToRepositoryRelative(root, p)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // A source that one project's build writes and another compiles is not missing for either.
        var localRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var writtenByBuild = ProjectFileChecks.WrittenByBuild(
            ProjectFileChecks.RepositoryImports(data.Evaluations, mapper.ToLocal, localRoot),
            ids.Select(id => RepoPaths.ToAbsolute(root, id)).Where(File.Exists));
        foreach (var id in ids)
        {
            var projectFile = RepoPaths.ToAbsolute(root, id);
            if (File.Exists(projectFile)
                && ProjectFileChecks.Check(root, projectFile, evaluations.GetValueOrDefault(id) ?? [], mapper.ToLocal, solutionDirectory, writtenByBuild) is { IsEmpty: false } findings)
            {
                result[id] = findings;
            }
        }

        return result;
    }

    /// <summary>
    /// One <c>OFR0122</c> per legacy project that the compile-only block does not reach, for a build that ran here
    /// outside Windows, where the block applies.
    /// </summary>
    private static IEnumerable<Diagnostic> ReportCompileOnlyGaps(ScanRequest request, BinlogData data, CapturePathMapper mapper, List<ProjectInfo> projects)
    {
        if (OperatingSystem.IsWindows() || !IsLocalCapture(mapper, request.RepositoryRoot))
        {
            return [];
        }

        var evaluated = projects.Select(p => (p.Id, (IReadOnlyList<EvaluatedProject>)[.. data.Evaluations.Where(e => mapper.ToRelative(e.ProjectFile) == p.Id)]));
        return [.. CompileOnlyReach.Find(request.RepositoryRoot, evaluated, mapper).Select(gap => request.Diagnostics.Report(DiagnosticCatalog.OFR0122,
            $"The compile-only block in {Doctor.CompileOnlyConditional.FileName} does not reach this project: {gap.Reason}.",
            new DiagnosticLocation(Project: gap.Project, File: gap.File),
            [KeyValuePair.Create<string, JsonNode?>("cause", gap.Cause), KeyValuePair.Create<string, JsonNode?>("file", gap.File)])!)];
    }

    /// <summary>The programs the build produces (<c>AssemblyName.exe</c> of each executable project) → the project.</summary>
    private static Dictionary<string, string> Executables(BinlogData data, CapturePathMapper mapper)
    {
        var executables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluation in data.Evaluations)
        {
            if (evaluation.Property("OutputType") is { } type && (type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
                && evaluation.Property("AssemblyName") is { } name && mapper.ToRelative(evaluation.ProjectFile) is { } project)
            {
                executables.TryAdd(name + ".exe", project);
            }
        }

        return executables;
    }

    /// <summary>
    /// One <c>OFR0123</c> per project that compiles files that do not exist. A git-ignored one is most likely
    /// written by the repository's own build script, which has to run before any build.
    /// </summary>
    private static async Task<List<Diagnostic>> ReportMissingSourcesAsync(
        ScanRequest request, IReadOnlyDictionary<string, ProjectFileFindings> files, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var reported = new List<Diagnostic>();
        var missing = files.Values.SelectMany(f => f.MissingSources).Select(p => RepoPaths.ToRepositoryRelative(root, p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (missing.Count == 0)
        {
            return reported;
        }

        var ignored = await GitIgnoredAsync(request, missing, cancellationToken);
        foreach (var (project, findings) in files.OrderBy(f => f.Key, StringComparer.Ordinal).Where(f => f.Value.MissingSources.Count > 0))
        {
            var paths = findings.MissingSources.Select(p => RepoPaths.ToRepositoryRelative(root, p)).ToList();
            var generated = paths.Where(ignored.Contains).ToList();
            var more = paths.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (and {paths.Count - 1} more)") : "";
            var hint = generated.Count > 0
                ? $" {generated[0]} is git-ignored, so the repository's own build (NAnt, psake, Cake, FAKE, GitVersion, ...) probably generates it: run that step first."
                : " Restore the file, or remove the item.";
            reported.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0123,
                $"Compiles a file that does not exist in any letter case: {paths[0]}{more}.{hint}",
                new DiagnosticLocation(Project: project, File: paths[0]),
                [
                    KeyValuePair.Create<string, JsonNode?>("file", paths[0]),
                    KeyValuePair.Create<string, JsonNode?>("files", new JsonArray([.. paths.Select(p => (JsonNode?)p)])),
                    KeyValuePair.Create<string, JsonNode?>("gitIgnored", new JsonArray([.. generated.Select(p => (JsonNode?)p)])),
                ])!);
        }

        return reported;
    }

    /// <summary>The repository-relative <paramref name="paths"/> git ignores; none without git or a repository.</summary>
    private static async Task<HashSet<string>> GitIgnoredAsync(ScanRequest request, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in paths.Chunk(200))
        {
            var result = await request.Processes.RunAsync(
                new ProcessSpec("git", ["check-ignore", "--", .. chunk]) { WorkingDirectory = request.RepositoryRoot, Timeout = TimeSpan.FromMinutes(1) },
                cancellationToken);
            if (result.NotFound || result.TimedOut || result.ExitCode > 1)
            {
                break;
            }

            ignored.UnionWith(result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(RepoPaths.Normalize));
        }

        return ignored;
    }

    private static IReadOnlyList<NotLoadedProject> FindNotLoaded(
        ScanRequest request, BinlogData data, CapturePathMapper mapper, List<ProjectInfo> projects, SolutionProjects? listed)
    {
        if (listed is null)
        {
            return [];
        }

        var loaded = projects.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var webSites = WebSites(request.RepositoryRoot, listed);
        var missing = listed.ProjectPaths.Select(p => RepoPaths.ToRepositoryRelative(request.RepositoryRoot, p).TrimEnd('/')).Where(id => !loaded.Contains(id)).ToList();
        // A project with errors of its own failed; one that is only missing from the log was not built either.
        var failed = data.Errors.Where(e => e.ProjectFile is not null)
            .Select(e => mapper.ToRelative(e.ProjectFile))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notBuilt = missing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restoreFailed = BuildFailures.RestoreFailed(data.Errors, text => Scrub(text, mapper));
        var nothingBuilt = BuildFailures.NothingBuilt(data.Errors, projects.Count, text => Scrub(text, mapper));
        var result = new List<NotLoadedProject>();
        foreach (var id in missing)
        {
            result.Add(new NotLoadedProject(id, NotLoadedReason(id)));
        }

        string NotLoadedReason(string id)
        {
            var root = request.RepositoryRoot;
            var extension = Path.GetExtension(id).ToLowerInvariant();
            if (webSites.Contains(id))
            {
                return WebSiteReason;
            }

            if (EvaluationError(data, mapper, id) is { } error)
            {
                return BuildFailures.IsRestoreError(error)
                    ? $"the restore failed ({BuildFailures.Label(error)}: {Scrub(BuildFailures.FirstLine(error.Message), mapper)})"
                    : $"{error.Code}: {error.Message}";
            }

            if (extension is not (".csproj" or ".vbproj" or ".fsproj"))
            {
                return $"unsupported project type ({(extension.Length > 0 ? extension : "no project file")})";
            }

            if (restoreFailed is not null)
            {
                return restoreFailed;
            }

            if (FailedReference(root, id, failed) is { } reference)
            {
                return $"not built: it references {reference}, which failed";
            }

            if (FailedSolutionDependency(root, id, listed, failed) is { } dependency)
            {
                return $"not built: the solution makes it depend on {dependency} (ProjectDependencies), which failed";
            }

            if (nothingBuilt is not null)
            {
                return nothingBuilt;
            }

            if (FailedReference(root, id, notBuilt) is { } unbuilt)
            {
                return $"not built: it references {unbuilt}, which was not built either";
            }

            return FailedSolutionDependency(root, id, listed, notBuilt) is { } unbuiltDependency
                ? $"not built: the solution makes it depend on {unbuiltDependency} (ProjectDependencies), which was not built either"
                : "no evaluation for it in the build log; MSBuild did not build it (check the solution configuration)";
        }

        return [.. result.OrderBy(r => r.Project, StringComparer.Ordinal)];
    }

    /// <summary>Why a Web Site project is not in the model.</summary>
    internal const string WebSiteReason =
        "ASP.NET Web Site project (a folder without a project file): only .NET Framework's MSBuild builds it, and Offramp does not model it";

    /// <summary>The repository-relative folders of the solution's Web Site projects.</summary>
    private static HashSet<string> WebSites(string root, SolutionProjects? listed) =>
        (listed?.WebSites ?? []).Select(p => RepoPaths.ToRepositoryRelative(root, p).TrimEnd('/')).ToHashSet(StringComparer.Ordinal);

    /// <summary>A Windows-only build step's diagnostic, with the build's paths made repository-relative, then shortened.</summary>
    internal static Diagnostic ReportStep(ScanRequest request, WindowsOnlyStep step, string project, CapturePathMapper mapper)
    {
        var evidence = WindowsOnlyBuildSteps.Shorten(Scrub(step.Evidence, mapper));
        var message = step.Id == "path-case"
            ? $"Does not build on a case-sensitive file system: {evidence}."
            : $"Needs Windows to build: {evidence}.";
        List<KeyValuePair<string, JsonNode?>> data = [KeyValuePair.Create<string, JsonNode?>("step", step.Id), KeyValuePair.Create<string, JsonNode?>("evidence", evidence)];
        if (step.Paths.Count > 0)
        {
            data.Add(KeyValuePair.Create<string, JsonNode?>("paths", new JsonArray([.. step.Paths.Select(p => (JsonNode?)(mapper.ToRelative(p) ?? Scrub(p, mapper)))])));
        }

        return request.Diagnostics.Report(step.Descriptor, message, new DiagnosticLocation(Project: project), data)!;
    }

    /// <summary>
    /// A project the project file references (read as XML, since there is no evaluation) that failed, which is
    /// why MSBuild did not build it; letter case is ignored, as the reference may be the reason.
    /// </summary>
    internal static string? FailedReference(string root, string project, IReadOnlySet<string> failed)
    {
        var path = RepoPaths.ToAbsolute(root, project);
        System.Xml.Linq.XDocument document;
        try
        {
            document = System.Xml.Linq.XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path)!;
        return document.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(include => !include.Contains("$(", StringComparison.Ordinal))
            .Select(include => RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/')))))
            .Where(failed.Contains)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// A project the solution makes this one depend on (<c>ProjectDependencies</c>) that failed: a mixed solution
    /// orders its native projects this way, and MSBuild builds nothing that depends on one that failed.
    /// </summary>
    internal static string? FailedSolutionDependency(string root, string project, SolutionProjects listed, IReadOnlySet<string> failed) =>
        listed.Dependencies.TryGetValue(RepoPaths.ToAbsolute(root, project), out var dependencies)
            ? dependencies.Select(d => RepoPaths.ToRepositoryRelative(root, d)).Where(failed.Contains).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;

    /// <summary>The first error the log records for a project, which explains why it has no evaluation.</summary>
    private static BuildError? EvaluationError(BinlogData data, CapturePathMapper mapper, string project) =>
        data.Errors.FirstOrDefault(e => e.ProjectFile is not null
            && string.Equals(mapper.ToRelative(e.ProjectFile), project, StringComparison.OrdinalIgnoreCase));

    /// <summary>One <c>OFR0121</c> per reference from a portable target to a framework-only project.</summary>
    internal static IEnumerable<Diagnostic> FrameworkOnlyReferences(DiagnosticBag diagnostics, IReadOnlyList<ProjectInfo> projects, ProjectGraph graph)
    {
        var byId = projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var edge in Readiness.FrameworkOnlyReferences(projects, graph))
        {
            var from = byId[edge.From];
            var to = byId[edge.To];
            var how = edge.Kind == GraphEdgeKind.Project ? "references" : "references the output of";
            var reported = diagnostics.Report(DiagnosticCatalog.OFR0121,
                $"{from.Id} ({string.Join(";", from.TargetFrameworks)}) {how} {to.Id}, which targets only .NET Framework ({string.Join(";", to.TargetFrameworks)}); it fails at run time on {from.Id}'s portable targets.",
                new DiagnosticLocation(Project: from.Id),
                [
                    KeyValuePair.Create<string, JsonNode?>("reference", to.Id),
                    KeyValuePair.Create<string, JsonNode?>("kind", edge.Kind == GraphEdgeKind.Project ? "project" : "assembly"),
                ]);
            if (reported is not null)
            {
                yield return reported;
            }
        }
    }

    /// <summary>A packages.config version as NuGet normalizes it (<c>1.0.0.0</c> is <c>1.0.0</c>), so both kinds of project share one spelling.</summary>
    private static string NormalizedVersion(string version) =>
        NuGet.Versioning.NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version;

    /// <summary>Package id → version → the projects using it, from PackageReference items and packages.config files.</summary>
    private static SortedDictionary<string, PackageUsage> PackageIndex(IEnumerable<ProjectInfo> projects)
    {
        var index = new SortedDictionary<string, SortedDictionary<string, SortedSet<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var used = project.PackageReferences
                .Select(p => (p.Id, Version: p.VersionOverride ?? p.Version ?? ResolvedVersion(project, p.Id) ?? "unknown"))
                .Concat((project.PackagesConfigPackages ?? []).Select(p => (p.Id, Version: NormalizedVersion(p.Version))));
            foreach (var (id, version) in used)
            {
                if (!index.TryGetValue(id, out var versions))
                {
                    index[id] = versions = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                }

                if (!versions.TryGetValue(version, out var users))
                {
                    versions[version] = users = new SortedSet<string>(StringComparer.Ordinal);
                }

                users.Add(project.Id);
            }
        }

        var result = new SortedDictionary<string, PackageUsage>(StringComparer.Ordinal);
        foreach (var (id, versions) in index)
        {
            var usage = new PackageUsage();
            foreach (var (version, users) in versions)
            {
                usage.Versions[version] = [.. users];
            }

            result[id] = usage;
        }

        return result;
    }

    private static string? ResolvedVersion(ProjectInfo project, string id) =>
        project.Resolved.Values.SelectMany(r => r.Packages)
            .FirstOrDefault(p => p.Direct && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Version;

    private static async Task<ScanOutcome> ScanComplogOnlyAsync(ScanRequest request, string state, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var complog = Path.Combine(state, ComplogFileName);
        IReadOnlyList<CompiledProject> compiled;
        using (request.Progress.BeginPhase("Reading the compiler log", 1, 3))
        {
            try
            {
                compiled = CompilerLogIngest.ReadProjects(request.ComplogPath!);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR0004,
                    $"'{Display(root, request.ComplogPath!)}' is not a readable compiler log: {ex.Message}");
                return new ScanOutcome(null, null, ScanFailure.Environment);
            }

            if (!string.Equals(Path.GetFullPath(request.ComplogPath!), Path.GetFullPath(complog), StringComparison.Ordinal))
            {
                Directory.CreateDirectory(state);
                File.Copy(request.ComplogPath!, complog, overwrite: true);
            }
        }

        request.Diagnostics.Report(DiagnosticCatalog.OFR0103,
            "The model was built from a compiler log alone; package references, SDK, test detection, and Windows-only build steps are unknown. Pass the matching --binlog for a full model.");

        WorkspaceModel model;
        using (request.Progress.BeginPhase("Building the workspace model", 2, 3))
        {
            var mapper = CapturePathMapper.Infer(root, compiled.Select(c => c.Call.ProjectFile));
            var complogRelative = RepoPaths.ToRepositoryRelative(root, complog);
            var projects = CompilerOnlyProjects.Build(compiled, mapper, request.Config, complogRelative);
            var graph = GraphBuilder.Build(projects);
            var diagnostics = new List<Diagnostic>();
            foreach (var cycle in graph.Cycles)
            {
                var path = GraphBuilder.CyclePath(cycle, graph.Edges);
                diagnostics.Add(request.Diagnostics.Report(DiagnosticCatalog.OFR0120,
                    $"Project reference cycle: {string.Join(" → ", path)}.", new DiagnosticLocation(Project: cycle[0]))!);
            }

            diagnostics.AddRange(FrameworkOnlyReferences(request.Diagnostics, projects, graph));

            model = new WorkspaceModel
            {
                CreatedAt = EnvelopeHeader.FormatTimestamp(request.Time.GetUtcNow()),
                RepositoryRoot = root,
                Solution = request.Config.Solution,
                Source = new WorkspaceSource(WorkspaceSourceKind.Complog, Display(root, request.ComplogPath!), ContentHash.Sha256File(request.ComplogPath!)),
                Sdk = new SdkInfo("unknown", "unknown"),
                Projects = projects,
                Graph = graph,
                Packages = PackageIndex(projects),
                Inputs = WorkspaceInputs.Collect(root, state, request.Config.Solution),
                Diagnostics = [.. diagnostics.OrderBy(d => d, DiagnosticOrder.Instance)],
            };
        }

        string ledger;
        using (request.Progress.BeginPhase("Writing the model and ledger snapshot", 3, 3))
        {
            WorkspaceStore.Save(request.WorkspacePath, model);
            ledger = Ledger.Write(Ledger.Snapshot(model), Path.GetFullPath(request.Config.Report.Ledger, root), root);
        }

        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new ScanOutcome(Summarize(model, request, ledger, upToDate: false, buildSucceeded: null, notLoaded: []), model, ScanFailure.None);
    }

    private static ScanResult Summarize(
        WorkspaceModel model, ScanRequest request, string? ledger, bool upToDate, bool? buildSucceeded, IReadOnlyList<NotLoadedProject> notLoaded)
    {
        var byClass = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var frameworkClass in Enum.GetValues<FrameworkClass>())
        {
            byClass[Wire(frameworkClass.ToString())] = model.Projects.Count(p => p.FrameworkClass == frameworkClass);
        }

        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<ProjectKind>())
        {
            byKind[Wire(kind.ToString())] = model.Projects.Count(p => p.Kind == kind);
        }

        return new ScanResult
        {
            Model = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, request.WorkspacePath),
            UpToDate = upToDate,
            Source = model.Source,
            Solution = model.Solution,
            BuildSucceeded = buildSucceeded,
            Projects = model.Projects.Count,
            Loc = model.Projects.Sum(p => p.Loc),
            ByFrameworkClass = byClass,
            ByKind = byKind,
            Cycles = model.Graph.Cycles,
            WindowsOnlyBuildSteps = [.. model.Projects.Where(p => p.WindowsOnlyBuildSteps.Count > 0)
                .Select(p => new WindowsOnlyProject(p.Id, p.WindowsOnlyBuildSteps))
                .Concat(notLoaded.Where(n => n.Project.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new WindowsOnlyProject(n.Project, ["ssdt"])))
                .Concat(notLoaded.Where(n => n.Reason == WebSiteReason).Select(n => new WindowsOnlyProject(n.Project, ["web-site"])))
                .OrderBy(p => p.Project, StringComparer.Ordinal)],
            Unrecognized = [.. model.Projects.Where(p => p.Kind == ProjectKind.Unknown).Select(p => p.Id)],
            NotLoaded = notLoaded,
            Partial = [.. model.Projects.Where(p => p.Partial).Select(p => p.Id)],
            LedgerSnapshot = ledger,
        };
    }

    private static string Wire(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>A path for output: repository-relative inside the repository, else just the file name.</summary>
    private static string Display(string root, string path)
    {
        var relative = RepoPaths.ToRepositoryRelative(root, path);
        return relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(path) : relative;
    }

    private static string Scrub(string problem, CapturePathMapper mapper) =>
        problem.Replace(mapper.CaptureRoot + "/", "", StringComparison.OrdinalIgnoreCase)
               .Replace(mapper.CaptureRoot.Replace('/', '\\') + "\\", "", StringComparison.OrdinalIgnoreCase);

    private static string FirstLines(string text, int count) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(count));
}
