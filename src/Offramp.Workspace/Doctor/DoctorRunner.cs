using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Output;
using Offramp.Core.Paths;
using Offramp.Workspace.Cpm;
using Offramp.Workspace.Environment;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Init;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

namespace Offramp.Workspace.Doctor;

/// <summary>
/// Runs <c>offramp doctor</c>'s checks in a fixed order and reports each with a
/// remedy. Read-only. See <c>docs/spec/commands/workspace.md#doctor</c>.
/// </summary>
public static class DoctorRunner
{
    private const int PhaseCount = 3;

    public static async Task<DoctorReport> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();
        var target = context.Config.Config.Target;

        DotnetSdkState sdk;
        using (context.Progress.BeginPhase("Checking .NET SDKs", 1, PhaseCount))
        {
            sdk = await new DotnetProbe(context.Processes).ProbeAsync(context.Repository.Path, cancellationToken);
        }

        var globalJson = GlobalJsonReader.Find(context.Repository.Path);
        checks.Add(CheckSdkInstalled(context, sdk));
        checks.Add(CheckSdkSelection(context, sdk, globalJson));
        checks.Add(CheckTarget(context, sdk, target));

        var model = TryReadModel(context);
        var files = model is null ? await SolutionProjectFilesAsync(context, cancellationToken) : null;
        using (context.Progress.BeginPhase("Checking .NET Framework reference assemblies", 2, PhaseCount))
        {
            checks.Add(LegacyReferenceAssemblies(context, model, files)
                ?? await CheckReferenceAssembliesAsync(context, NetFrameworkTargets(model, files), cancellationToken));
        }

        string? gitVersion;
        using (context.Progress.BeginPhase("Checking git and repository", 3, PhaseCount))
        {
            gitVersion = await context.Git.GetVersionAsync(cancellationToken);
        }

        checks.Add(CheckGit(context, gitVersion));
        checks.Add(CheckRepository(context, gitVersion));
        checks.Add(CheckConfig(context));
        checks.Add(CheckWorkspace(context, model));
        var guardFiles = GuardFiles(context.Repository.Path, model, files?.Select(f => f.Project));
        checks.Add(CheckWindowsOnlySteps(context, model));
        checks.Add(CheckPlainBuild(context, model, files, guardFiles));
        checks.Add(await CheckCpmAsync(context, model, cancellationToken));

        return new DoctorReport
        {
            Fix = context.Fix ? PlanFix(context.Repository.Path, guardFiles, UsesPackagesConfig(context.Repository.Path, model, files?.Select(f => f.Project))) : null,
            Checks = checks,
            Environment = new DoctorEnvironment
            {
                Sdks = sdk.Installed,
                SelectedSdk = sdk.Selected,
                GlobalJson = globalJson is null
                    ? null
                    : new GlobalJsonInfo(
                        RepoPaths.ToRepositoryRelative(context.Repository.Path, globalJson.AbsolutePath),
                        globalJson.Version,
                        globalJson.RollForward),
                Git = new GitInfo(gitVersion, context.Repository.Source == RepositoryRootSource.Git),
                Os = context.Os,
                Target = target.Moniker,
            },
            Summary = new DoctorSummary(
                checks.Count(c => c.Status == CheckStatus.Pass),
                checks.Count(c => c.Status == CheckStatus.Warn),
                checks.Count(c => c.Status == CheckStatus.Fail),
                checks.Count(c => c.Status == CheckStatus.Skip)),
        };
    }

    private static DoctorCheck CheckSdkInstalled(DoctorContext context, DotnetSdkState sdk)
    {
        const string id = "dotnet-sdk";
        const string title = ".NET SDK installed";
        if (!sdk.HostFound || sdk.Installed.Count == 0)
        {
            var message = sdk.HostFound
                ? "The dotnet host is installed but no SDK is."
                : "`dotnet` could not be started.";
            Report(context, DiagnosticCatalog.OFR0010, message);
            return Fail(id, title, message,
                "Install the .NET SDK from https://dot.net and make sure `dotnet --list-sdks` lists it.",
                DiagnosticCatalog.OFR0010);
        }

        return Pass(id, title, $"Installed: {string.Join(", ", sdk.Installed)}.");
    }

    private static DoctorCheck CheckSdkSelection(DoctorContext context, DotnetSdkState sdk, GlobalJsonReader.Found? globalJson)
    {
        const string id = "global-json";
        const string title = "SDK selection (global.json)";
        if (!sdk.HostFound || sdk.Installed.Count == 0)
        {
            return Skip(id, title, "Skipped: no .NET SDK.");
        }

        var globalJsonPath = globalJson is null
            ? null
            : RepoPaths.ToRepositoryRelative(context.Repository.Path, globalJson.AbsolutePath);
        if (sdk.Selected is null)
        {
            var requested = globalJson?.Version ?? "?";
            var file = globalJsonPath ?? "global.json";
            var message = $"{file} requests SDK {requested} (rollForward {globalJson?.RollForward ?? "default"}), and none of the installed SDKs ({string.Join(", ", sdk.Installed)}) satisfies it.";
            Report(context, DiagnosticCatalog.OFR0011, message, data:
            [
                KeyValuePair.Create<string, JsonNode?>("requested", requested),
                KeyValuePair.Create<string, JsonNode?>("rollForward", globalJson?.RollForward),
                KeyValuePair.Create<string, JsonNode?>("dotnet", sdk.SelectionError),
            ]);
            var remedy = RollForwardFor(requested, sdk.Installed) is { } policy
                ? $"Install .NET SDK {requested}, or set `sdk.rollForward` to `{policy}` in {file}, the least permissive setting that selects an installed SDK."
                : $"Install .NET SDK {requested} or newer; no installed SDK is.";
            return Fail(id, title, message, remedy, DiagnosticCatalog.OFR0011);
        }

        var detail = globalJson is null
            ? $"No global.json; dotnet uses the newest SDK, {sdk.Selected}."
            : $"{globalJsonPath} requests {globalJson.Version ?? "any"} (rollForward {globalJson.RollForward ?? "default"}); dotnet selects {sdk.Selected}.";
        return Pass(id, title, detail);
    }

    /// <summary>
    /// The least permissive <c>rollForward</c> that lets <paramref name="requested"/> select an installed
    /// SDK: <c>latestFeature</c> (same major and minor), <c>latestMinor</c> (same major), or
    /// <c>latestMajor</c>; null when every installed SDK is older.
    /// </summary>
    internal static string? RollForwardFor(string requested, IReadOnlyList<string> installed)
    {
        if (Numeric(requested) is not { } wanted)
        {
            return null;
        }

        var newer = installed.Select(Numeric).OfType<Version>().Where(v => v >= wanted).ToList();
        return newer.Count == 0 ? null
            : newer.Any(v => v.Major == wanted.Major && v.Minor == wanted.Minor) ? "latestFeature"
            : newer.Any(v => v.Major == wanted.Major) ? "latestMinor"
            : "latestMajor";

        static Version? Numeric(string version) => Version.TryParse(version.Split('-')[0], out var parsed) ? parsed : null;
    }

    private static DoctorCheck CheckTarget(DoctorContext context, DotnetSdkState sdk, ModernTarget modern)
    {
        const string id = "target";
        var moniker = modern.Moniker;
        var title = $"SDK can target {moniker}";
        var major = DotnetProbe.Major(sdk.Selected);
        if (major is null)
        {
            return Skip(id, title, "Skipped: no SDK selected.");
        }

        // Every SDK builds .NET Standard 2.x; applications and tests move to .NET 10 under it (ADR 0057).
        var target = modern.RuntimeMajor;
        if (major < target)
        {
            var message = modern.IsStandard
                ? $"SDK {sdk.Selected} cannot build net{target}.0, which applications and tests move to under {moniker}; it targets up to net{major}.0."
                : $"SDK {sdk.Selected} cannot build {moniker}; it targets up to net{major}.0.";
            Report(context, DiagnosticCatalog.OFR0012, message, data:
            [
                KeyValuePair.Create<string, JsonNode?>("selected", sdk.Selected),
                KeyValuePair.Create<string, JsonNode?>("target", moniker),
            ]);
            return Fail(id, title, message,
                $"Install the .NET {target} SDK and update global.json if it pins an older one, or pass `--target {major}`.",
                DiagnosticCatalog.OFR0012);
        }

        return Pass(id, title, $"SDK {sdk.Selected} can build {moniker}.");
    }

    /// <summary>
    /// The project files of the configured (or chosen) solution, read before the first scan; empty when there is
    /// no solution to read.
    /// </summary>
    private static async Task<IReadOnlyList<ProjectFileFacts>> SolutionProjectFilesAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var solution = context.Config.Config.Solution
            ?? (await SolutionChooser.ChooseAsync(root, InitPlanner.FindSolutions(root), cancellationToken)).Solution;
        var solutionPath = solution is null ? null : RepoPaths.ToAbsolute(root, solution);
        if (solutionPath is null || !File.Exists(solutionPath))
        {
            return [];
        }

        try
        {
            var listed = await SolutionReader.ReadAsync(solutionPath, cancellationToken);
            return [.. listed.ProjectPaths.Select(p => ProjectFileFacts.Read(root, RepoPaths.ToRepositoryRelative(root, p))).OfType<ProjectFileFacts>()];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The .NET Framework targets the projects compile for, from the model or, before a scan, the project files.</summary>
    private static IReadOnlyList<string> NetFrameworkTargets(WorkspaceModel? model, IReadOnlyList<ProjectFileFacts>? files) =>
        [.. (model is not null
                ? model.Projects.SelectMany(p => p.TargetFrameworks).Select(ProjectFileFacts.ReferenceAssembliesName).OfType<string>()
                : (files ?? []).SelectMany(f => f.NetFrameworkTargets))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Outside Windows, legacy projects get the reference assemblies only through the compile-only block's
    /// legacy section (docs/decisions/0037-legacy-projects-outside-windows.md); a cached package does not reach them.
    /// Before the first scan the solution's project files tell which projects are legacy.
    /// </summary>
    private static DoctorCheck? LegacyReferenceAssemblies(DoctorContext context, WorkspaceModel? model, IReadOnlyList<ProjectFileFacts>? files)
    {
        var legacy = model?.Projects.Count(p => !p.SdkStyle && p.FrameworkClass != FrameworkClass.Modern && p.FrameworkClass != FrameworkClass.Standard)
            ?? (files ?? []).Count(f => f.Legacy);
        var propsPath = Path.Combine(context.Repository.Path, CompileOnlyConditional.FileName);
        if (OperatingSystem.IsWindows() || legacy == 0 || CompileOnlyConditional.HasLegacySection(File.Exists(propsPath) ? File.ReadAllText(propsPath) : null))
        {
            return null;
        }

        var message = string.Create(CultureInfo.InvariantCulture,
            $"{legacy} legacy (non-SDK) project(s) get no reference assemblies from the SDK, and {CompileOnlyConditional.FileName} has no legacy section to supply them.");
        Report(context, DiagnosticCatalog.OFR0018, message);
        return Warn("reference-assemblies", ".NET Framework reference assemblies", message,
            "Run `offramp doctor --fix --apply` to add the compile-only block's legacy section.", DiagnosticCatalog.OFR0018);
    }

    /// <param name="context">The doctor's context.</param>
    /// <param name="frameworks">The .NET Framework targets the projects compile for; none probes net48.</param>
    /// <param name="cancellationToken">Cancels feed queries.</param>
    private static async Task<DoctorCheck> CheckReferenceAssembliesAsync(DoctorContext context, IReadOnlyList<string> frameworks, CancellationToken cancellationToken)
    {
        const string id = "reference-assemblies";
        const string title = ".NET Framework reference assemblies";
        var result = await context.ReferenceAssemblies.ProbeAsync(context.Repository.Path, frameworks, cancellationToken);
        IReadOnlyList<string> about = result.Frameworks.Count > 0 ? result.Frameworks : frameworks.Count > 0 ? frameworks : [IReferenceAssembliesProbe.DefaultFramework];
        var packages = string.Join(", ", about.Select(t => ReferenceAssembliesProbe.PackagePrefix + t));
        var targets = string.Join(", ", about);
        switch (result.State)
        {
            case ReferenceAssembliesState.Cached:
                return Pass(id, title, $"The reference assemblies for {targets} are in the NuGet global packages folder ({result.Detail}).");
            case ReferenceAssembliesState.TargetingPack:
                return Pass(id, title, $"The .NET Framework targeting packs for {targets} are installed ({result.Detail}).");
            case ReferenceAssembliesState.AvailableFromFeed:
                return Pass(id, title, $"Not cached yet for {targets}; feed '{result.Detail}' provides {packages}, so the first build downloads them.");
            case ReferenceAssembliesState.FeedUnreachable:
                {
                    var message = $"Not cached for {targets}, and feed(s) {result.Detail} could not be queried, so net4x builds may fail offline.";
                    Report(context, DiagnosticCatalog.OFR1006, message,
                        data: [KeyValuePair.Create<string, JsonNode?>("feeds", result.Detail)]);
                    return Warn(id, title, message,
                        "Check network access and nuget.config credentials, or restore once on a connected machine.",
                        DiagnosticCatalog.OFR1006);
                }

            default:
                {
                    var message = $"{packages} {(about.Count == 1 ? "is" : "are")} neither cached nor on any configured feed.";
                    Report(context, DiagnosticCatalog.OFR0013, message);
                    return Fail(id, title, message,
                        "Add nuget.org (or a mirror carrying the package) to nuget.config.",
                        DiagnosticCatalog.OFR0013);
                }
        }
    }

    private static DoctorCheck CheckGit(DoctorContext context, string? gitVersion)
    {
        const string id = "git";
        const string title = "git installed";
        if (gitVersion is null)
        {
            const string message = "`git` could not be started; moves would be plain file moves and nothing would be staged.";
            Report(context, DiagnosticCatalog.OFR0014, message);
            return Warn(id, title, message, "Install git and put it on PATH.", DiagnosticCatalog.OFR0014);
        }

        return Pass(id, title, $"git {gitVersion}.");
    }

    private static DoctorCheck CheckRepository(DoctorContext context, string? gitVersion)
    {
        const string id = "git-repository";
        const string title = "git repository";
        if (gitVersion is null)
        {
            return Skip(id, title, "Skipped: git is not available.");
        }

        if (context.Repository.Source != RepositoryRootSource.Git)
        {
            const string message = "Not inside a git work tree; moves would use plain file moves.";
            Report(context, DiagnosticCatalog.OFR0015, message);
            return Warn(id, title, message, "Run Offramp inside a git clone of the repository.", DiagnosticCatalog.OFR0015);
        }

        return Pass(id, title, "Repository detected; moves are staged with `git mv`.");
    }

    private static DoctorCheck CheckConfig(DoctorContext context)
    {
        const string id = "config";
        const string title = "offramp.yml";
        var diagnostics = context.Config.Diagnostics;
        var codes = diagnostics.Select(d => d.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var file = context.Config.File ?? ConfigLoader.DefaultFileName;

        if (diagnostics.Any(d => d.Severity == Severity.Error))
        {
            var count = diagnostics.Count(d => d.Severity == Severity.Error);
            return new DoctorCheck
            {
                Id = id, Title = title, Status = CheckStatus.Fail,
                Message = $"{file} has {count} error(s); commands refuse to run until they are fixed.",
                Remedy = "Fix the errors listed in the diagnostics; schemas/v1/config.json documents every key.",
                Codes = codes,
            };
        }

        if (diagnostics.Any(d => d.Severity == Severity.Warning))
        {
            var count = diagnostics.Count(d => d.Severity == Severity.Warning);
            return new DoctorCheck
            {
                Id = id, Title = title, Status = CheckStatus.Warn,
                Message = $"{file} is usable with {count} warning(s).",
                Remedy = "Fix the warnings listed in the diagnostics.",
                Codes = codes,
            };
        }

        var message = context.Config.File is null
            ? "No offramp.yml; built-in defaults are in effect."
            : $"{file} is valid.";
        return new DoctorCheck
        {
            Id = id, Title = title, Status = CheckStatus.Pass, Message = message,
            Remedy = context.Config.File is null ? "Run `offramp init` to write one with detected values." : null,
            Codes = codes,
        };
    }

    private static WorkspaceModel? TryReadModel(DoctorContext context)
    {
        if (!File.Exists(context.WorkspacePath))
        {
            return null;
        }

        try
        {
            return WorkspaceStore.Read(context.WorkspacePath);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static DoctorCheck CheckWorkspace(DoctorContext context, WorkspaceModel? model)
    {
        const string id = "workspace";
        const string title = "Workspace model";
        var relative = RepoPaths.ToRepositoryRelative(context.Repository.Path, context.WorkspacePath);
        if (model is null)
        {
            var message = File.Exists(context.WorkspacePath)
                ? $"{relative} is unreadable (written by another Offramp version?)."
                : $"{relative} does not exist yet.";
            Report(context, DiagnosticCatalog.OFR0001, message, severity: Severity.Warning,
                data: [KeyValuePair.Create<string, JsonNode?>("path", relative)]);
            return Warn(id, title, message, "Run `offramp scan`.", DiagnosticCatalog.OFR0001);
        }

        var state = WorkspaceStore.StateDirectory(context.Repository.Path, context.Config.Config);
        var staleness = WorkspaceInputs.Compare(model, context.Repository.Path, state);
        if (staleness.IsStale)
        {
            var message = $"{relative} is stale: {staleness.Describe()}.";
            Report(context, DiagnosticCatalog.OFR0002, message);
            return Warn(id, title, message, "Run `offramp scan` (or `offramp scan --if-stale`).", DiagnosticCatalog.OFR0002);
        }

        return Pass(id, title, $"{relative} is up to date ({model.Projects.Count} projects, scanned {model.CreatedAt}).");
    }

    private static DoctorCheck CheckWindowsOnlySteps(DoctorContext context, WorkspaceModel? model)
    {
        const string id = "windows-only-build-steps";
        const string title = "Windows-only build steps";
        if (model is null)
        {
            return Skip(id, title, "Skipped: run `offramp scan` to detect them.");
        }

        var stepDiagnostics = model.Diagnostics
            .Where(d => WindowsOnlyBuildSteps.IsStepCode(d.Code))
            .ToList();
        var byProject = stepDiagnostics
            .Where(d => d.Project is not null)
            .GroupBy(d => d.Project!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(d => d.Data.TryGetValue("step", out var s) ? s?.ToString() : d.Code).Distinct())})")
            .ToList();
        if (byProject.Count == 0)
        {
            var properties = context.Config.Config.Verify.Properties.Keys.Order(StringComparer.Ordinal).ToList();
            return Pass(id, title, properties.Count == 0
                ? "No project needs Windows to build."
                : $"No project needs Windows to build with offramp.yml's verify.properties ({string.Join(", ", properties)}); the plain-build check says what a build without them does.");
        }

        foreach (var diagnostic in stepDiagnostics)
        {
            context.Diagnostics.Add(diagnostic);
        }

        var propsPath = Path.Combine(context.Repository.Path, CompileOnlyConditional.FileName);
        var hasBlock = CompileOnlyConditional.IsPresent(File.Exists(propsPath) ? File.ReadAllText(propsPath) : null);
        var remedy = hasBlock
            ? "The compile-only block is present; `offramp doctor --fix --apply` conditions the Windows-only settings project files set themselves. Guard any other step with Condition=\"'$(OS)' == 'Windows_NT'\", or scan a compiler log captured on Windows."
            : "Run `offramp doctor --fix --apply`: it adds the compile-only block to Directory.Build.props and conditions the Windows-only settings project files set themselves. Guard any other step with Condition=\"'$(OS)' == 'Windows_NT'\", or scan a compiler log captured on Windows.";
        return new DoctorCheck
        {
            Id = id,
            Title = title,
            Status = CheckStatus.Warn,
            Message = $"{byProject.Count} project(s) need Windows to build: {string.Join("; ", byProject)}.",
            Remedy = remedy,
            Codes = [.. stepDiagnostics.Select(d => d.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// The MSBuild files whose Windows-only settings <c>--fix</c> conditions: the projects of the model or, before
    /// the first scan, of the solution, the shared files they import, and a settings file a scan named as the reason
    /// the compile-only block does not reach a project (<c>OFR0122</c>).
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="model">The workspace model, when there is one.</param>
    /// <param name="projects">Without a model, the solution's projects, repository-relative.</param>
    public static IReadOnlyList<string> GuardFiles(string repositoryRoot, WorkspaceModel? model, IEnumerable<string>? projects) =>
        WindowsGuards.Files(
            repositoryRoot,
            model?.Projects.Select(p => p.Id) ?? projects ?? [],
            (model?.Diagnostics ?? [])
                .Where(d => d.Code == DiagnosticCatalog.OFR0122.Code && d.Data.TryGetValue("cause", out var cause) && cause?.ToString() == "msbuild-extensions-path")
                .Select(d => d.Data.TryGetValue("file", out var file) ? file?.ToString() : null)
                .OfType<string>());

    /// <summary>True when a project of the model (before the first scan, of the solution) restores from packages.config.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="model">The workspace model, when there is one.</param>
    /// <param name="projects">Without a model, the solution's projects, repository-relative.</param>
    public static bool UsesPackagesConfig(string repositoryRoot, WorkspaceModel? model, IEnumerable<string>? projects) =>
        PackagesConfigProjects(repositoryRoot, model, projects) > 0;

    private static int PackagesConfigProjects(string root, WorkspaceModel? model, IEnumerable<string>? projects) =>
        model?.Projects.Count(p => p.PackagesConfig)
            ?? (projects ?? []).Count(p => File.Exists(Path.Combine(Path.GetDirectoryName(RepoPaths.ToAbsolute(root, p))!, "packages.config")));

    /// <summary>True when <c>dotnet restore</c> restores packages.config: the targets file, its import in the block, and in the solution file.</summary>
    private static bool PackagesConfigRestored(string root)
    {
        var props = Path.Combine(root, CompileOnlyConditional.FileName);
        return PackagesConfigRestore.IsInPlace(root) && CompileOnlyConditional.HasPackagesConfigSection(File.Exists(props) ? File.ReadAllText(props) : null);
    }

    /// <summary>
    /// Whether a plain <c>dotnet build</c> of the solution does what Offramp's own build does, so that a scan that
    /// builds cleanly means the solution builds for anyone who clones it (docs/decisions/0063-condition-windows-only-settings-in-project-files.md):
    /// no Windows-only setting without a condition (<c>OFR0019</c>), and nothing only Offramp's builds supply
    /// (<c>OFR0026</c>). It lists what a build outside Windows skips, the settings conditioned on Windows.
    /// </summary>
    private static DoctorCheck CheckPlainBuild(DoctorContext context, WorkspaceModel? model, IReadOnlyList<ProjectFileFacts>? files, IReadOnlyList<string> guardFiles)
    {
        const string id = "plain-build";
        const string title = "Builds without Offramp";
        var root = context.Repository.Path;
        var found = guardFiles.Select(f => (File: f, Found: WindowsGuards.Find(root, f))).ToList();
        var needed = found.SelectMany(f => f.Found.Needed.Select(g => (f.File, Guard: g))).ToList();
        var guarded = found.SelectMany(f => f.Found.Guarded.Select(g => (f.File, Guard: g))).ToList();
        var gaps = new List<string>();
        var remedies = new List<string>();
        var codes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (file, guard) in needed)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0019,
                $"{guard.Setting} ({guard.Step}) has no condition, so a plain dotnet build outside Windows runs it and fails; condition it on {guard.Condition}.",
                new DiagnosticLocation(File: file, Line: guard.Line),
                [
                    KeyValuePair.Create<string, JsonNode?>("setting", guard.Setting),
                    KeyValuePair.Create<string, JsonNode?>("step", guard.Step),
                    KeyValuePair.Create<string, JsonNode?>("condition", guard.Condition),
                ]);
        }

        if (needed.Count > 0)
        {
            codes.Add(DiagnosticCatalog.OFR0019.Code);
            gaps.Add(string.Create(CultureInfo.InvariantCulture,
                $"{needed.Count} Windows-only setting(s) in {needed.Select(n => n.File).Distinct(StringComparer.Ordinal).Count()} file(s) have no condition ({Sites(needed)})"));
            remedies.Add("Run `offramp doctor --fix --apply` to condition them.");
        }

        var properties = context.Config.Config.Verify.Properties.Keys.Order(StringComparer.Ordinal).ToList();
        if (properties.Count > 0)
        {
            var message = $"offramp.yml's verify.properties passes {string.Join(", ", properties)} to Offramp's builds only; a plain dotnet build does not get them.";
            context.Diagnostics.Report(DiagnosticCatalog.OFR0026, message, data: [KeyValuePair.Create<string, JsonNode?>("cause", "verify-properties")]);
            codes.Add(DiagnosticCatalog.OFR0026.Code);
            gaps.Add($"verify.properties passes {string.Join(", ", properties)}");
            remedies.Add("Set what verify.properties sets in the project files instead (doctor --fix conditions the Windows-only settings), then remove it from offramp.yml.");
        }

        var packagesConfig = PackagesConfigProjects(root, model, files?.Select(f => f.Project));
        var restored = "";
        if (packagesConfig > 0 && PackagesConfigRestored(root))
        {
            restored = string.Create(CultureInfo.InvariantCulture, $" `dotnet restore` restores the {packagesConfig} packages.config project(s) through {PackagesConfigRestore.TargetsFileName}.");
        }
        else if (packagesConfig > 0)
        {
            var message = string.Create(CultureInfo.InvariantCulture,
                $"{packagesConfig} project(s) restore from packages.config, which dotnet restore skips, and the repository has no {PackagesConfigRestore.TargetsFileName} to restore it; Offramp's scan restores them into the packages folder, so a fresh clone needs that first.");
            context.Diagnostics.Report(DiagnosticCatalog.OFR0026, message, data: [KeyValuePair.Create<string, JsonNode?>("cause", "packages-config")]);
            codes.Add(DiagnosticCatalog.OFR0026.Code);
            gaps.Add(string.Create(CultureInfo.InvariantCulture, $"{packagesConfig} project(s) use packages.config, which dotnet restore skips"));
            remedies.Add($"Run `offramp doctor --fix --apply`: it adds {PackagesConfigRestore.TargetsFileName}, which has dotnet restore restore packages.config.");
        }

        var webSites = (model?.Diagnostics ?? []).Where(d => d.Code == DiagnosticCatalog.OFR0126.Code).Select(d => d.Project ?? d.File).OfType<string>().Order(StringComparer.Ordinal).ToList();
        if (webSites.Count > 0)
        {
            var message = $"The solution lists ASP.NET Web Site project(s) ({string.Join(", ", webSites)}); dotnet build stops the whole solution on them (MSB4249), and Offramp's scan builds a solution filter without them.";
            context.Diagnostics.Report(DiagnosticCatalog.OFR0026, message, data: [KeyValuePair.Create<string, JsonNode?>("cause", "web-site")]);
            codes.Add(DiagnosticCatalog.OFR0026.Code);
            gaps.Add($"the solution lists ASP.NET Web Site project(s): {string.Join(", ", webSites)}");
            remedies.Add("Convert a Web Site to a web application project, or build a solution filter without it.");
        }

        var skipped = guarded.Count == 0 ? "" : $" Outside Windows it skips {guarded.Count} conditioned setting(s) that Visual Studio's build runs ({Sites(guarded)}).";
        if (gaps.Count == 0)
        {
            return Pass(id, title, "A plain `dotnet build` of the solution does what Offramp's build does." + restored + skipped);
        }

        return new DoctorCheck
        {
            Id = id,
            Title = title,
            Status = CheckStatus.Warn,
            Message = $"A plain `dotnet build` does less than Offramp's build: {string.Join("; ", gaps)}." + restored + skipped,
            Remedy = string.Join(" ", remedies),
            Codes = [.. codes],
        };

        static string Sites(List<(string File, WindowsGuard Guard)> sites) =>
            string.Join(", ", sites.Take(5).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.File}:{s.Guard.Line} {s.Guard.Setting}")))
            + (sites.Count > 5 ? ", …" : "");
    }

    private static async Task<DoctorCheck> CheckCpmAsync(DoctorContext context, WorkspaceModel? model, CancellationToken cancellationToken)
    {
        const string id = "cpm";
        const string title = "Central package management";
        var root = context.Repository.Path;
        IReadOnlyCollection<string>? projects = model?.Projects.Select(p => p.Id).ToList();
        if (projects is null)
        {
            var solution = context.Config.Config.Solution
                ?? (await SolutionChooser.ChooseAsync(root, InitPlanner.FindSolutions(root), cancellationToken)).Solution;
            var solutionPath = solution is null ? null : RepoPaths.ToAbsolute(root, solution);
            if (solutionPath is null || !File.Exists(solutionPath))
            {
                return Skip(id, title, "Skipped: no workspace model or solution to compare against.");
            }

            try
            {
                var listed = await SolutionReader.ReadAsync(solutionPath, cancellationToken);
                projects = [.. listed.ProjectPaths.Select(p => RepoPaths.ToRepositoryRelative(root, p))];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                return Skip(id, title, $"Skipped: {solution} could not be read.");
            }
        }

        // packages.config projects (OFR1303) matter only once central package management is in use;
        // `deps consolidate --cpm` runs the full preflight itself.
        var hazards = CpmHazards.Find(root, projects);
        var inUse = CpmHazards.AnyCentralVersions(root) || File.Exists(RepoPaths.ToAbsolute(root, context.Config.Config.Deps.Cpm.File));
        hazards = [.. hazards.Where(h => inUse || h.Descriptor != DiagnosticCatalog.OFR1303)];
        if (hazards.Count == 0)
        {
            return Pass(id, title, inUse ? "No central package management hazards." : "Central package management is not in use.");
        }

        foreach (var hazard in hazards)
        {
            context.Diagnostics.Report(hazard.Descriptor, hazard.Message,
                hazard.Descriptor == DiagnosticCatalog.OFR1302 ? new DiagnosticLocation(File: hazard.Path) : new DiagnosticLocation(Project: hazard.Path));
        }

        return new DoctorCheck
        {
            Id = id,
            Title = title,
            Status = CheckStatus.Warn,
            Message = $"{hazards.Count} hazard(s): " + string.Join("; ", hazards.Take(5).Select(h => $"{h.Descriptor.Code} {h.Path}")) + (hazards.Count > 5 ? "; …" : "") + ".",
            Remedy = "Keep central versions in a non-default file (deps.cpm.file) with per-project opt-in, fix nested props files to import the root one, and migrate packages.config projects.",
            Codes = [.. hazards.Select(h => h.Descriptor.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// What <c>--fix</c> would change: the compile-only block in the root Directory.Build.props, a condition on
    /// each Windows-only setting in <paramref name="guardFiles"/> (<see cref="GuardFiles"/>), and with
    /// <paramref name="packagesConfig"/> the files that let <c>dotnet restore</c> restore packages.config
    /// (<see cref="PackagesConfigRestore"/>).
    /// </summary>
    public static CompileOnlyFix PlanFix(string repositoryRoot, IReadOnlyList<string>? guardFiles = null, bool packagesConfig = false)
    {
        var path = Path.Combine(repositoryRoot, CompileOnlyConditional.FileName);
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        var updated = CompileOnlyConditional.Apply(current, packagesConfig);
        return new CompileOnlyFix
        {
            File = CompileOnlyConditional.FileName,
            AlreadyPresent = updated is null,
            Applied = false,
            Diff = updated is null
                ? null
                : UnifiedDiff.Create(current is null ? null : CompileOnlyConditional.FileName, CompileOnlyConditional.FileName, current ?? "", updated),
            ProjectFiles = [.. (guardFiles ?? []).Select(f => WindowsGuards.Plan(repositoryRoot, f)).OfType<ProjectFileFix>()],
            PackagesConfigFiles = packagesConfig ? PackagesConfigRestore.Plan(repositoryRoot) : [],
            PackagesConfig = packagesConfig,
        };
    }

    /// <summary>
    /// Conditions the Windows-only settings in <paramref name="guardFiles"/>, writes the packages.config restore files
    /// with <paramref name="packagesConfig"/>, then the compile-only block; returns the fix with <c>Applied</c> set on
    /// what was written.
    /// </summary>
    public static CompileOnlyFix ApplyFix(string repositoryRoot, IReadOnlyList<string>? guardFiles = null, bool packagesConfig = false)
    {
        var plan = PlanFix(repositoryRoot, guardFiles, packagesConfig);
        plan = plan with
        {
            ProjectFiles = [.. plan.ProjectFiles.Select(f => WindowsGuards.Apply(repositoryRoot, f.File) ?? f)],
            PackagesConfigFiles = packagesConfig ? PackagesConfigRestore.Apply(repositoryRoot) : [],
        };
        if (plan.AlreadyPresent)
        {
            return plan;
        }

        var path = Path.Combine(repositoryRoot, CompileOnlyConditional.FileName);
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var current = bytes is null ? null : new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        File.WriteAllText(path, CompileOnlyConditional.Apply(current, packagesConfig)!, new System.Text.UTF8Encoding(hasBom));
        return plan with { Applied = true };
    }

    private static void Report(
        DoctorContext context, DiagnosticDescriptor descriptor, string message,
        Severity? severity = null, IEnumerable<KeyValuePair<string, JsonNode?>>? data = null) =>
        context.Diagnostics.Report(descriptor, message, data: data, severity: severity);

    private static DoctorCheck Pass(string id, string title, string message) =>
        new() { Id = id, Title = title, Status = CheckStatus.Pass, Message = message };

    private static DoctorCheck Skip(string id, string title, string message) =>
        new() { Id = id, Title = title, Status = CheckStatus.Skip, Message = message };

    private static DoctorCheck Warn(string id, string title, string message, string remedy, DiagnosticDescriptor code) =>
        new() { Id = id, Title = title, Status = CheckStatus.Warn, Message = message, Remedy = remedy, Codes = [code.Code] };

    private static DoctorCheck Fail(string id, string title, string message, string remedy, DiagnosticDescriptor code) =>
        new() { Id = id, Title = title, Status = CheckStatus.Fail, Message = message, Remedy = remedy, Codes = [code.Code] };
}
