using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Output;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Init;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

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
    private const int MaxErrorsInMessage = 5;

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
            solution ??= ResolveSolution(request);
            if (solution is null)
            {
                return new ScanOutcome(null, null, request.Diagnostics.Contains("OFR0020") ? ScanFailure.Usage : ScanFailure.Environment);
            }

            binlog = Path.Combine(state, BinlogFileName);
            kind = WorkspaceSourceKind.Build;
            using (request.Progress.BeginPhase($"Building {solution}", ++phase, plan))
            {
                var built = await BuildAsync(request, solution, binlog, cancellationToken);
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
            var callMap = MapCalls(calls, mapper, RepoPaths.ToRepositoryRelative(root, complog));
            var defines = calls
                .Where(c => mapper.ToRelative(c.ProjectFile) is not null && c.TargetFramework is not null)
                .GroupBy(c => (mapper.ToRelative(c.ProjectFile)!, c.TargetFramework!))
                .ToDictionary(g => g.Key, g => g.First().Defines);
            var source = new WorkspaceSource(kind, Display(root, binlog), ContentHash.Sha256File(binlog))
            {
                Complog = request.ComplogPath is null ? null : new LogFile(Display(root, request.ComplogPath), ContentHash.Sha256File(request.ComplogPath)),
            };
            (model, notLoaded) = await BuildModelAsync(request, data, mapper, callMap, defines, source, solution, state, cancellationToken);
        }

        string ledgerPath;
        using (request.Progress.BeginPhase("Writing the model and ledger snapshot", ++phase, plan))
        {
            WorkspaceStore.Save(request.WorkspacePath, model);
            ledgerPath = Ledger.Write(Ledger.Snapshot(model), Path.GetFullPath(request.Config.Report.Ledger, root), root);
        }

        return new ScanOutcome(Summarize(model, request, ledgerPath, upToDate: false, buildSucceeded, notLoaded), model, ScanFailure.None);
    }

    private static string? ResolveSolution(ScanRequest request)
    {
        var candidates = InitPlanner.FindSolutions(request.RepositoryRoot);
        var chosen = InitPlanner.ChooseSolution(candidates);
        if (chosen is not null)
        {
            return chosen;
        }

        if (candidates.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0022,
                "No .sln or .slnx file in the repository. Pass --solution, or scan a log with --binlog.");
        }
        else
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0020,
                $"Found {candidates.Count} solutions ({string.Join(", ", candidates.Take(5))}); pass --solution or set solution: in offramp.yml.",
                data: [KeyValuePair.Create<string, JsonNode?>("candidates", new JsonArray([.. candidates.Select(c => (JsonNode?)c)]))]);
        }

        return null;
    }

    private static async Task<ProcessResult?> BuildAsync(ScanRequest request, string solution, string binlog, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(binlog)!);
        var arguments = new List<string>
        {
            "build", RepoPaths.ToAbsolute(request.RepositoryRoot, solution),
            "-bl:" + binlog, "-c", request.Config.Verify.Configuration,
            "-nologo", "-v:minimal", "-clp:NoSummary", "-nodeReuse:false",
        };
        foreach (var (name, value) in request.Config.Verify.Properties)
        {
            arguments.Add($"-p:{name}={value}");
        }

        var result = await request.Processes.RunAsync(new ProcessSpec("dotnet", arguments)
        {
            WorkingDirectory = request.RepositoryRoot,
            Timeout = TimeSpan.FromSeconds(request.Config.Verify.TimeoutSeconds),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        }, cancellationToken);

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
                $"The build of {solution} produced no binary log: {FirstLines(detail, 3)}");
            return null;
        }

        return result;
    }

    private static void ReportBuildErrors(ScanRequest request, BinlogData data, CapturePathMapper mapper)
    {
        if (data.Succeeded && data.Errors.Count == 0)
        {
            return;
        }

        var errors = data.Errors
            .Select(e => $"{(e.File is null ? "" : (mapper.ToRelative(e.File) ?? Path.GetFileName(e.File)) + (e.Line is null ? "" : $"({e.Line})") + ": ")}{e.Code}: {e.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var shown = string.Join("; ", errors.Take(MaxErrorsInMessage));
        request.Diagnostics.Report(DiagnosticCatalog.OFR0130,
            errors.Count == 0
                ? "The build failed; the model is partial."
                : $"The build failed with {errors.Count} error(s); the model is partial. First: {shown}",
            data:
            [
                KeyValuePair.Create<string, JsonNode?>("errorCount", errors.Count),
                KeyValuePair.Create<string, JsonNode?>("errors", new JsonArray([.. errors.Take(20).Select(e => (JsonNode?)e)])),
            ]);
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
            var report = CompilerLogIngest.Convert(binlog, complog);
            if (report.Problems.Count > 0)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR0132,
                    $"{report.Problems.Count} problem(s) converting the build log; affected projects have no compiler call. First: {report.Problems[0]}",
                    data: [KeyValuePair.Create<string, JsonNode?>("problems", new JsonArray([.. report.Problems.Take(20).Select(p => (JsonNode?)Scrub(p, mapper))]))]);
            }

            if (!File.Exists(complog))
            {
                return [];
            }
        }

        return CompilerLogIngest.ReadCalls(complog);
    }

    private static bool IsLocalCapture(CapturePathMapper mapper, string repositoryRoot) =>
        string.Equals(mapper.CaptureRoot, repositoryRoot.Replace('\\', '/').TrimEnd('/'),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static Dictionary<(string Project, string Tfm), CompilerCallRef> MapCalls(
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
            map.TryAdd((project, tfm), new CompilerCallRef(complogRelative, call.Index));
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
        var context = new ProjectBuildContext
        {
            Paths = mapper,
            Config = request.Config,
            CompilerCalls = calls,
            CompilerDefines = defines,
            Excluded = new PathGlobs(request.Config.Paths.Exclude),
        };

        var projects = new List<ProjectInfo>();
        foreach (var group in data.Evaluations.GroupBy(e => e.ProjectFile, StringComparer.Ordinal))
        {
            var id = mapper.ToRelative(group.Key);
            if (id is null)
            {
                continue;
            }

            projects.Add(ProjectModelBuilder.Build(id, [.. group], context));
        }

        projects.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var notLoaded = await FindNotLoadedAsync(request, data, mapper, projects, solution, cancellationToken);
        var loading = new List<Diagnostic>();
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
            }
        }

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

            foreach (var step in WindowsOnlyBuildSteps.Detect(project.Id, evaluations))
            {
                loading.Add(request.Diagnostics.Report(step.Descriptor,
                    $"Needs Windows to build: {step.Evidence}.",
                    new DiagnosticLocation(Project: project.Id),
                    [KeyValuePair.Create<string, JsonNode?>("step", step.Id), KeyValuePair.Create<string, JsonNode?>("evidence", step.Evidence)])!);
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
            Inputs = WorkspaceInputs.Collect(root, state, solution),
            Diagnostics = [.. loading.Where(d => d is not null).OrderBy(d => d, DiagnosticOrder.Instance)],
        };
        return (model, notLoaded);
    }

    private static async Task<IReadOnlyList<NotLoadedProject>> FindNotLoadedAsync(
        ScanRequest request, BinlogData data, CapturePathMapper mapper, List<ProjectInfo> projects, string? solution,
        CancellationToken cancellationToken)
    {
        if (solution is null)
        {
            return [];
        }

        var solutionPath = RepoPaths.ToAbsolute(request.RepositoryRoot, solution);
        if (!File.Exists(solutionPath))
        {
            return [];
        }

        SolutionProjects listed;
        try
        {
            listed = await SolutionReader.ReadAsync(solutionPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return [];
        }

        var loaded = projects.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<NotLoadedProject>();
        foreach (var path in listed.ProjectPaths)
        {
            var id = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, path);
            if (loaded.Contains(id))
            {
                continue;
            }

            var error = data.Errors.FirstOrDefault(e => e.ProjectFile is not null
                && string.Equals(mapper.ToRelative(e.ProjectFile), id, StringComparison.OrdinalIgnoreCase));
            var extension = Path.GetExtension(id).ToLowerInvariant();
            var reason = error is not null
                ? $"{error.Code}: {error.Message}"
                : extension is ".csproj" or ".vbproj" or ".fsproj"
                    ? "no evaluation for it in the build log"
                    : $"unsupported project type ({extension})";
            result.Add(new NotLoadedProject(id, reason));
        }

        return [.. result.OrderBy(r => r.Project, StringComparer.Ordinal)];
    }

    private static SortedDictionary<string, PackageUsage> PackageIndex(IEnumerable<ProjectInfo> projects)
    {
        var index = new SortedDictionary<string, SortedDictionary<string, SortedSet<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            foreach (var package in project.PackageReferences)
            {
                var version = package.VersionOverride ?? package.Version ?? ResolvedVersion(project, package.Id) ?? "unknown";
                if (!index.TryGetValue(package.Id, out var versions))
                {
                    index[package.Id] = versions = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
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
