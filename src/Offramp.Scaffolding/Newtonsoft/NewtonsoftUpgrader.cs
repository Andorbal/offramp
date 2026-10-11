using System.IO.Compression;
using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Newtonsoft;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.NuGet.Feeds;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Dependencies.PackagesConfig;
using Offramp.Refactoring.Dependencies.Redirects;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Restore;
using Offramp.Workspace.Verification;

namespace Offramp.Scaffolding.Newtonsoft;

/// <summary>A project the upgrade changes, and the files it edits for it.</summary>
public sealed record UpgradedProject(string Project, string From, NewtonsoftReferenceKind Via, IReadOnlyList<string> Files);

/// <summary>The <c>result</c> of <c>offramp newtonsoft upgrade</c> (<c>schemas/v1/newtonsoft-upgrade.json</c>).</summary>
public sealed record NewtonsoftUpgradeResult
{
    public required string To { get; init; }

    /// <summary>The releases in use before, with the projects on each.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> From { get; init; }

    public required IReadOnlyList<UpgradedProject> Projects { get; init; }

    /// <summary>What <c>redirects sync</c> does over the upgraded model.</summary>
    public RedirectsResult? Redirects { get; init; }

    /// <summary>The scan's findings for the projects upgraded: what to expect.</summary>
    public required IReadOnlyList<NewtonsoftFinding> Findings { get; init; }

    /// <summary>The build after the upgrade; null in a dry run.</summary>
    public VerifyResult? Verify { get; init; }

    /// <summary>The probes compared with their baselines after the upgrade.</summary>
    public IReadOnlyList<ProbeOutcome> Probes { get; init; } = [];

    /// <summary><c>keep</c> or <c>rollback</c>: what happens when probes differ.</summary>
    public required string OnDiff { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public bool RolledBack { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Preview { get; init; }
}

public sealed record NewtonsoftUpgradeRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required NuGetVersion Target { get; init; }

    /// <summary>The target's .nupkg: its <c>lib/</c> folders name the HintPaths, and redirects read its assemblies.</summary>
    public required byte[] TargetPackage { get; init; }

    /// <summary>Project ids to upgrade; empty for every project that uses Newtonsoft.Json.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary><c>keep</c> or <c>rollback</c>.</summary>
    public string OnDiff { get; init; } = "keep";

    public required IProcessRunner Processes { get; init; }

    public required IGitService Git { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// <c>offramp newtonsoft upgrade</c> (docs/spec/commands/newtonsoft.md#newtonsoft-upgrade; ADR 0072):
/// every reference style moved to the target as one change set, redirects synced over the
/// upgraded model, then restore, build, and the probes compared, rolled back on failure.
/// </summary>
public static class NewtonsoftUpgrader
{
    private const string Id = NewtonsoftReferences.PackageId;

    public static async Task<(NewtonsoftUpgradeResult Result, ChangeSet? ChangeSet)> PlanAsync(NewtonsoftUpgradeRequest request, CancellationToken cancellationToken)
    {
        var scan = NewtonsoftScanner.Scan(new NewtonsoftScanRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = request.Model,
            Target = request.Target,
            TargetPackage = request.TargetPackage,
            Projects = request.Projects,
            Diagnostics = new DiagnosticBag(),
            Progress = request.Progress,
            EntryDepth = request.Config.Newtonsoft.Tests.EntryDepth,
            NonHermetic = request.Config.Newtonsoft.Hermetic.NonHermetic,
        }, cancellationToken);

        var changeSet = new ChangeSet();
        var upgraded = new List<UpgradedProject>();
        var from = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var centralFiles = new HashSet<string>(StringComparer.Ordinal);
        var assemblyVersion = $"{request.Target.Major}.{request.Target.Minor}.0.0";
        var libFolders = LibFolders(request.TargetPackage);
        foreach (var project in Candidates(request))
        {
            var tfm = CompilationLoader.PreferredTarget(project)!;
            var reference = NewtonsoftReferences.For(project, tfm)!;
            if (!from.TryGetValue(reference.Version, out var users))
            {
                from[reference.Version] = users = [];
            }

            users.Add(project.Id);
            if (NuGetVersion.TryParse(reference.Version, out var current) && current == request.Target)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3724, $"{project.Id} already uses Newtonsoft.Json {request.Target}.", new DiagnosticLocation(project.Id));
                continue;
            }

            Dependents(request, project);
            var files = reference.Via switch
            {
                NewtonsoftReferenceKind.LooseDll => Loose(request, project, reference),
                NewtonsoftReferenceKind.Transitive => [],
                NewtonsoftReferenceKind.CentralPackage => Central(request, project, changeSet, centralFiles),
                NewtonsoftReferenceKind.PackageReference => PackageReference(request, project, changeSet),
                _ => Legacy(request, project, reference, assemblyVersion, Nearest(libFolders, tfm), changeSet),
            };
            if (files is not null)
            {
                upgraded.Add(new UpgradedProject(project.Id, reference.Version, reference.Via, files));
            }
        }

        var redirects = await RedirectsAsync(request, changeSet, cancellationToken).ConfigureAwait(false);
        var result = new NewtonsoftUpgradeResult
        {
            To = request.Target.ToNormalizedString(),
            From = from.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal),
            Projects = upgraded,
            Redirects = redirects,
            Findings = scan.Findings,
            OnDiff = request.OnDiff,
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return (result, changeSet.IsEmpty ? null : changeSet);
    }

    /// <summary>
    /// Applies the change set with a journal, restores, builds, and compares the probes. A failed
    /// restore or build rolls everything back (OFR3766, OFR3761); probes that differ are OFR3762 and
    /// roll back only with <c>--on-diff rollback</c>.
    /// </summary>
    public static async Task<NewtonsoftUpgradeResult> ApplyAsync(NewtonsoftUpgradeRequest request, NewtonsoftUpgradeResult planned, ChangeSet changeSet, CancellationToken cancellationToken)
    {
        var applier = new ChangeSetApplier(request.RepositoryRoot, request.Git);
        var journal = await applier.ApplyAsync(changeSet, "newtonsoft upgrade", request.Time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var result = planned with { Applied = true, Journal = journal, Preview = null };

        if (await RestoreAsync(request, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3766, $"Restore failed after the upgrade, which was rolled back: {failure}");
            return await RollBackAsync(request, applier, result, cancellationToken).ConfigureAwait(false);
        }

        var selection = VerifySelector.Select(request.Model, null, null, request.Config.Verify.Projects);
        var build = await VerifyRunner.RunAsync(new VerifyRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = request.Model,
            Config = request.Config.Verify,
            Mode = Enum.Parse<VerifyMode>(request.Config.Verify.Mode, ignoreCase: true),
            Projects = selection.Projects,
            Everything = selection.Everything,
            Scope = selection.Scope,
            TargetFramework = request.Config.TargetFramework,
            Processes = request.Processes,
            Diagnostics = request.Diagnostics,
            Progress = request.Progress,
        }, cancellationToken).ConfigureAwait(false);
        result = result with { Verify = build };
        if (build is not { Passed: true })
        {
            var predicted = planned.Findings.Where(f => f.Rule == "OFR3701").Select(f => $"{f.File}:{f.Line}").ToList();
            request.Diagnostics.Report(DiagnosticCatalog.OFR3761,
                $"The solution does not build on Newtonsoft.Json {planned.To} ({build?.ErrorCount ?? 0} error(s); the first: {ProbeRecorder.Describe(build?.Errors.FirstOrDefault()?.First) ?? "the build did not start"}); the upgrade was rolled back."
                + (predicted.Count > 0 ? $" `newtonsoft scan` predicted it (OFR3701 at {string.Join(", ", predicted.Take(5))})." : ""));
            return await RollBackAsync(request, applier, result, cancellationToken).ConfigureAwait(false);
        }

        var (tests, _) = NewtonsoftTestsGenerator.Generate(new NewtonsoftTestsRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = request.Model,
            TestProjects = request.Config.Newtonsoft.Tests.Projects,
            Folder = request.Config.Newtonsoft.Tests.Folder,
            SamplesPerType = request.Config.Newtonsoft.Tests.SamplesPerType,
            ObjectDepth = request.Config.Newtonsoft.Tests.ObjectDepth,
            EntryDepth = request.Config.Newtonsoft.Tests.EntryDepth,
            NonHermetic = request.Config.Newtonsoft.Hermetic.NonHermetic,
            Diagnostics = new DiagnosticBag(),
        }, cancellationToken);
        var recorded = tests.Targets.Where(t => Directory.Exists(RepoPaths.ToAbsolute(request.RepositoryRoot, t.Folder + "/Baseline"))).ToList();
        if (recorded.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3767,
                "No characterization baselines were recorded (`offramp newtonsoft tests --record --apply` on the old release), so the upgrade was checked by the compiler only.");
            return result;
        }

        var verified = await ProbeRecorder.VerifyAsync(new ProbeRunRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = request.Model,
            Config = request.Config,
            Targets = recorded,
            Probes = [.. tests.Probes.Where(p => recorded.Any(t => t.TestProject == p.TestProject))],
            Processes = request.Processes,
            Diagnostics = request.Diagnostics,
            Progress = request.Progress,
        }, cancellationToken).ConfigureAwait(false);
        result = result with { Probes = verified.Outcomes };
        var changed = verified.Outcomes.Any(o => o.Status is ProbeStatus.Differs or ProbeStatus.NowThrows or ProbeStatus.NoLongerThrows);
        return changed && request.OnDiff == "rollback" ? await RollBackAsync(request, applier, result, cancellationToken).ConfigureAwait(false) : result;
    }

    /// <summary>The C# projects that use Newtonsoft.Json (test projects too: their identity must stay the production one's).</summary>
    private static IEnumerable<ProjectInfo> Candidates(NewtonsoftUpgradeRequest request) =>
        request.Model.Projects
            .Where(p => p.Language == "csharp" && (request.Projects.Count == 0 || request.Projects.Contains(p.Id, StringComparer.Ordinal)))
            .Where(p => CompilationLoader.PreferredTarget(p) is { } tfm && NewtonsoftReferences.For(p, tfm) is not null)
            .OrderBy(p => p.Id, StringComparer.Ordinal);

    /// <summary>OFR3765 for packages whose dependency range on Newtonsoft.Json leaves out the target.</summary>
    private static void Dependents(NewtonsoftUpgradeRequest request, ProjectInfo project)
    {
        foreach (var package in project.Resolved.Values.SelectMany(r => r.Packages).DistinctBy(p => p.Id + "/" + p.Version).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dependency in package.Dependencies.Where(d => string.Equals(d.Id, Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (VersionRange.TryParse(dependency.Range, out var range) && !range.Satisfies(request.Target))
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR3765,
                        $"{package.Id} {package.Version} depends on Newtonsoft.Json {range.PrettyPrint()}, which leaves out {request.Target}; restore will report NU1608 or fail. Upgrade {package.Id} too.",
                        new DiagnosticLocation(project.Id));
                }
            }
        }
    }

    private static IReadOnlyList<string>? Loose(NewtonsoftUpgradeRequest request, ProjectInfo project, NewtonsoftReference reference)
    {
        request.Diagnostics.Report(DiagnosticCatalog.OFR3723,
            $"{project.Id} references {reference.HintPath}, which no package provides; it is not upgraded. Run `offramp deps resolve-dlls` first.", new DiagnosticLocation(project.Id));
        return null;
    }

    private static IReadOnlyList<string> PackageReference(NewtonsoftUpgradeRequest request, ProjectInfo project, ChangeSet changeSet)
    {
        var before = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id));
        var editor = ProjectFileEditor.Load(before);
        editor.SetMetadata("PackageReference", Id, "Version", request.Target.ToNormalizedString());
        changeSet.Edit(project.Id, before, editor.Save());
        return [project.Id];
    }

    /// <summary>The central file's PackageVersion (once per file), or the project's VersionOverride.</summary>
    private static IReadOnlyList<string> Central(NewtonsoftUpgradeRequest request, ProjectInfo project, ChangeSet changeSet, HashSet<string> done)
    {
        if (project.PackageReferences.Any(r => string.Equals(r.Id, Id, StringComparison.OrdinalIgnoreCase) && r.VersionOverride is not null))
        {
            var before = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id));
            var editor = ProjectFileEditor.Load(before);
            editor.SetMetadata("PackageReference", Id, "VersionOverride", request.Target.ToNormalizedString());
            changeSet.Edit(project.Id, before, editor.Save());
            return [project.Id];
        }

        if (CentralFile(request.RepositoryRoot, project) is not { } central || !done.Add(central))
        {
            return [];
        }

        var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, central));
        var file = ProjectFileEditor.Load(bytes);
        file.SetMetadata("PackageVersion", Id, "Version", request.Target.ToNormalizedString());
        changeSet.Edit(central, bytes, file.Save());
        return [central];
    }

    private static string? CentralFile(string root, ProjectInfo project)
    {
        if (project.Properties.TryGetValue("DirectoryPackagesPropsPath", out var recorded) && File.Exists(RepoPaths.ToAbsolute(root, recorded)))
        {
            return RepoPaths.Normalize(recorded);
        }

        var directory = project.Id.Contains('/', StringComparison.Ordinal) ? project.Id[..project.Id.LastIndexOf('/')] : "";
        while (true)
        {
            var candidate = directory.Length == 0 ? "Directory.Packages.props" : directory + "/Directory.Packages.props";
            if (File.Exists(RepoPaths.ToAbsolute(root, candidate)))
            {
                return candidate;
            }

            if (directory.Length == 0)
            {
                return null;
            }

            directory = directory.Contains('/', StringComparison.Ordinal) ? directory[..directory.LastIndexOf('/')] : "";
        }
    }

    /// <summary>packages.config's version (unless its allowedVersions leaves out the target), and the Reference's identity and HintPath folders.</summary>
    private static List<string>? Legacy(NewtonsoftUpgradeRequest request, ProjectInfo project, NewtonsoftReference reference, string assemblyVersion, string? libFolder, ChangeSet changeSet)
    {
        var files = new List<string>();
        var folder = project.Id.Contains('/', StringComparison.Ordinal) ? project.Id[..(project.Id.LastIndexOf('/') + 1)] : "";
        var config = folder + "packages.config";
        var configPath = RepoPaths.ToAbsolute(request.RepositoryRoot, config);
        if (reference.Via == NewtonsoftReferenceKind.PackagesConfig && File.Exists(configPath))
        {
            var bytes = File.ReadAllBytes(configPath);
            if (PackagesConfigEditor.Find(bytes, Id) is { AllowedVersions: { } allowed } && VersionRange.TryParse(allowed, out var range) && !range.Satisfies(request.Target))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3764,
                    $"{config} allows Newtonsoft.Json {allowed} only, which leaves out {request.Target}; {project.Id} is not upgraded. Widen allowedVersions first.",
                    new DiagnosticLocation(project.Id, config));
                return null;
            }

            if (PackagesConfigEditor.SetVersion(bytes, Id, request.Target.ToNormalizedString()) is { } updated)
            {
                changeSet.Edit(config, bytes, updated);
                files.Add(config);
            }
        }

        var before = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id));
        var editor = ProjectFileEditor.Load(before);
        if (editor.RewriteReference(Id, assemblyVersion, $"{Id}.{reference.Version}", $"{Id}.{request.Target.ToNormalizedString()}", libFolder) > 0)
        {
            changeSet.Edit(project.Id, before, editor.Save());
            files.Add(project.Id);
        }

        return files;
    }

    /// <summary>
    /// <c>redirects sync</c> over the model as the upgrade leaves it, with the target's package in
    /// the global packages folder (outside the repository), merged into the change set.
    /// </summary>
    private static async Task<RedirectsResult?> RedirectsAsync(NewtonsoftUpgradeRequest request, ChangeSet changeSet, CancellationToken cancellationToken)
    {
        await GlobalPackages.EnsureAsync(request.RepositoryRoot, Id, request.Target, request.TargetPackage, cancellationToken).ConfigureAwait(false);
        var plan = RedirectPlanner.Plan(new RedirectsRequest
        {
            RepositoryRoot = request.RepositoryRoot,
            Model = Upgraded(request.Model, request.Target),
            Diagnostics = new DiagnosticBag(),
        });
        foreach (var edit in plan.ChangeSet?.Edits ?? [])
        {
            changeSet.Edit(edit.Path, edit.Before, edit.After);
        }

        return plan.Result with { Preview = null };
    }

    /// <summary>The model with every Newtonsoft.Json reference at the target.</summary>
    private static WorkspaceModel Upgraded(WorkspaceModel model, NuGetVersion target)
    {
        var version = target.ToNormalizedString();
        var assembly = $"{target.Major}.{target.Minor}.0.0";
        bool Is(string id) => string.Equals(id, Id, StringComparison.OrdinalIgnoreCase);
        return model with
        {
            Projects = [.. model.Projects.Select(p => p with
            {
                PackagesConfigPackages = p.PackagesConfigPackages?.Select(c => Is(c.Id) ? c with { Version = version } : c).ToList(),
                PackageReferences = [.. p.PackageReferences.Select(r => Is(r.Id) && r.Version is not null ? r with { Version = version } : r)],
                Resolved = new SortedDictionary<string, ResolvedFramework>(
                    p.Resolved.ToDictionary(r => r.Key, r => r.Value with { Packages = [.. r.Value.Packages.Select(k => Is(k.Id) ? k with { Version = version } : k)] }),
                    StringComparer.Ordinal),
                AssemblyReferences = [.. p.AssemblyReferences.Select(r => Is(r.Name) && r.HintPath is { } hint
                    ? r with
                    {
                        HintPath = string.Join('/', hint.Split('/').Select(s => s.StartsWith(Id + ".", StringComparison.OrdinalIgnoreCase) && NuGetVersion.TryParse(s[(Id.Length + 1)..], out _) ? $"{Id}.{version}" : s)),
                        Metadata = r.Metadata is null ? null : r.Metadata with { AssemblyVersion = assembly },
                    }
                    : r)],
            })],
        };
    }

    /// <summary>Restores the solution (and its packages.config projects); null when it succeeded, else why not.</summary>
    private static async Task<string?> RestoreAsync(NewtonsoftUpgradeRequest request, CancellationToken cancellationToken)
    {
        var solution = request.Model.Solution is { } relative ? RepoPaths.ToAbsolute(request.RepositoryRoot, relative) : null;
        var restore = await request.Processes.RunAsync(new ProcessSpec("dotnet", solution is null ? ["restore", "-nologo"] : ["restore", solution, "-nologo"])
        {
            WorkingDirectory = request.RepositoryRoot,
            Timeout = TimeSpan.FromSeconds(request.Config.Verify.TimeoutSeconds),
        }, cancellationToken).ConfigureAwait(false);
        if (!restore.Succeeded)
        {
            return $"`dotnet restore` exited with {restore.ExitCode}: " + string.Join(" ", (restore.StandardOutput + restore.StandardError).Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(3).Select(l => l.Trim()));
        }

        var projects = request.Model.Projects.Select(p => RepoPaths.ToAbsolute(request.RepositoryRoot, p.Id)).ToList();
        if (solution is null || !PackagesConfigRestorer.HasPackagesConfig(projects))
        {
            return null;
        }

        var legacy = await PackagesConfigRestorer.RestoreAsync(solution, projects, cancellationToken).ConfigureAwait(false);
        return legacy is { Failed.Count: > 0 } ? "packages.config restore failed for " + string.Join(", ", legacy.Failed.Select(f => $"{f.Id} {f.Version} ({f.Reason})")) : null;
    }

    private static async Task<NewtonsoftUpgradeResult> RollBackAsync(NewtonsoftUpgradeRequest request, ChangeSetApplier applier, NewtonsoftUpgradeResult result, CancellationToken cancellationToken)
    {
        await applier.RollbackAsync(result.Journal!, cancellationToken).ConfigureAwait(false);
        // Leave the restored state matching the files again (best effort; the files are what matter).
        await RestoreAsync(request, cancellationToken).ConfigureAwait(false);
        return result with { RolledBack = true };
    }

    /// <summary>The target framework folders under the package's <c>lib/</c> that hold Newtonsoft.Json.dll.</summary>
    private static List<string> LibFolders(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return [.. archive.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && e.Name.Equals(Id + ".dll", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') == 2)
            .Select(e => e.FullName.Split('/')[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The <c>lib/</c> folder NuGet would pick for the project's framework.</summary>
    private static string? Nearest(List<string> folders, string tfm) =>
        NuGetFrameworkUtility.GetNearest(folders, NuGetFramework.Parse(tfm), f => NuGetFramework.ParseFolder(f));
}
