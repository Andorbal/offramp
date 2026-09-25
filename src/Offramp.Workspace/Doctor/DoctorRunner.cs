using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Workspace.Environment;

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
        var targetMoniker = OfframpConfig.TargetMoniker(target);

        DotnetSdkState sdk;
        using (context.Progress.BeginPhase("Checking .NET SDKs", 1, PhaseCount))
        {
            sdk = await new DotnetProbe(context.Processes).ProbeAsync(context.Repository.Path, cancellationToken);
        }

        var globalJson = GlobalJsonReader.Find(context.Repository.Path);
        checks.Add(CheckSdkInstalled(context, sdk));
        checks.Add(CheckSdkSelection(context, sdk, globalJson));
        checks.Add(CheckTarget(context, sdk, target, targetMoniker));

        using (context.Progress.BeginPhase("Checking .NET Framework reference assemblies", 2, PhaseCount))
        {
            checks.Add(await CheckReferenceAssembliesAsync(context, cancellationToken));
        }

        string? gitVersion;
        using (context.Progress.BeginPhase("Checking git and repository", 3, PhaseCount))
        {
            gitVersion = await context.Git.GetVersionAsync(cancellationToken);
        }

        checks.Add(CheckGit(context, gitVersion));
        checks.Add(CheckRepository(context, gitVersion));
        checks.Add(CheckConfig(context));
        checks.Add(CheckWorkspace(context));

        return new DoctorReport
        {
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
                Target = targetMoniker,
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
            var message = $"{globalJsonPath ?? "global.json"} requests SDK {requested}, which is not installed: {sdk.SelectionError}";
            Report(context, DiagnosticCatalog.OFR0011, message, data:
            [
                KeyValuePair.Create<string, JsonNode?>("requested", requested),
                KeyValuePair.Create<string, JsonNode?>("rollForward", globalJson?.RollForward),
            ]);
            return Fail(id, title, message,
                $"Install .NET SDK {requested}, or relax `sdk.rollForward` in {globalJsonPath ?? "global.json"} (for example `latestFeature`).",
                DiagnosticCatalog.OFR0011);
        }

        var detail = globalJson is null
            ? $"No global.json; dotnet uses the newest SDK, {sdk.Selected}."
            : $"{globalJsonPath} requests {globalJson.Version ?? "any"} (rollForward {globalJson.RollForward ?? "default"}); dotnet selects {sdk.Selected}.";
        return Pass(id, title, detail);
    }

    private static DoctorCheck CheckTarget(DoctorContext context, DotnetSdkState sdk, int target, string moniker)
    {
        const string id = "target";
        var title = $"SDK can target {moniker}";
        var major = DotnetProbe.Major(sdk.Selected);
        if (major is null)
        {
            return Skip(id, title, "Skipped: no SDK selected.");
        }

        if (major < target)
        {
            var message = $"SDK {sdk.Selected} cannot build {moniker}; it targets up to net{major}.0.";
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

    private static async Task<DoctorCheck> CheckReferenceAssembliesAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        const string id = "reference-assemblies";
        const string title = ".NET Framework reference assemblies";
        var result = await context.ReferenceAssemblies.ProbeAsync(context.Repository.Path, cancellationToken);
        switch (result.State)
        {
            case ReferenceAssembliesState.Cached:
                return Pass(id, title, $"{ReferenceAssembliesProbe.PackageId} {result.Detail} is in the NuGet global packages folder.");
            case ReferenceAssembliesState.TargetingPack:
                return Pass(id, title, $"The .NET Framework {result.Detail} targeting pack is installed.");
            case ReferenceAssembliesState.AvailableFromFeed:
                return Pass(id, title, $"Not cached yet; feed '{result.Detail}' provides {ReferenceAssembliesProbe.PackageId}, so the first net4x build downloads it.");
            case ReferenceAssembliesState.FeedUnreachable:
                {
                    var message = $"Not cached, and feed(s) {result.Detail} could not be queried, so net4x builds may fail offline.";
                    Report(context, DiagnosticCatalog.OFR1006, message,
                        data: [KeyValuePair.Create<string, JsonNode?>("feeds", result.Detail)]);
                    return Warn(id, title, message,
                        "Check network access and nuget.config credentials, or restore once on a connected machine.",
                        DiagnosticCatalog.OFR1006);
                }

            default:
                {
                    var message = $"{ReferenceAssembliesProbe.PackageId} is neither cached nor on any configured feed.";
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

    private static DoctorCheck CheckWorkspace(DoctorContext context)
    {
        const string id = "workspace";
        const string title = "Workspace model";
        var relative = RepoPaths.ToRepositoryRelative(context.Repository.Path, context.WorkspacePath);
        if (!File.Exists(context.WorkspacePath))
        {
            var message = $"{relative} does not exist yet.";
            Report(context, DiagnosticCatalog.OFR0001, message, severity: Severity.Warning,
                data: [KeyValuePair.Create<string, JsonNode?>("path", relative)]);
            return Warn(id, title, message, "Run `offramp scan`.", DiagnosticCatalog.OFR0001);
        }

        return Pass(id, title, $"{relative} exists.");
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
