using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Offramp.Workspace.Store;
using Offramp.Workspace.Verification;

namespace Offramp.Scaffolding.Csproj;

/// <summary>What <see cref="ModernizeVerifier"/> checks.</summary>
public sealed record ModernizeVerifyRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required ChangeSet ChangeSet { get; init; }

    /// <summary>The projects whose project files change.</summary>
    public required IReadOnlyList<string> Projects { get; init; }

    /// <summary>packages.config entries that became PackageReference items, in any project.</summary>
    public IReadOnlyList<(string Id, string Version)> ConvertedPackages { get; init; } = [];

    public required IGitService Git { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>
/// Proves a conversion changes nothing the compiler sees: builds each changed project in a
/// scratch copy with the change set applied, reads its compiler calls from the binary log,
/// and compares source files, references, and embedded resources with the scan's build of
/// the original (docs/spec/commands/scaffold.md#csproj-modernize). A difference or a failed
/// build is <c>OFR4303</c>.
/// </summary>
public static class ModernizeVerifier
{
    public static async Task<SortedDictionary<string, ModernizeVerification>> VerifyAsync(ModernizeVerifyRequest request, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var result = new SortedDictionary<string, ModernizeVerification>(StringComparer.Ordinal);
        var beforeLog = request.Model.Source.Kind == WorkspaceSourceKind.Complog || request.Model.Source.Complog is null
            ? RepoPaths.ToAbsolute(root, request.Model.Source.Path)
            : RepoPaths.ToAbsolute(root, request.Model.Source.Complog.Path);
        if (!File.Exists(beforeLog))
        {
            foreach (var project in request.Projects)
            {
                Fail(request, project, $"the scan's build log {request.Model.Source.Path} is gone, so there is nothing to compare with; run `offramp scan`.");
                result[project] = new ModernizeVerification { Passed = false };
            }

            return result;
        }

        await using var scratch = await ScratchWorktree.CreateAsync(root, Files(request), request.Git, cancellationToken);
        ReportUncommitted(request, scratch);
        Apply(request.ChangeSet, scratch);
        var index = 0;
        foreach (var project in request.Projects.Order(StringComparer.Ordinal))
        {
            using var phase = request.Progress.BeginPhase($"Building the converted {project}", ++index, request.Projects.Count);
            result[project] = await VerifyProjectAsync(request, scratch, beforeLog, project, index, cancellationToken);
        }

        return result;
    }

    private static async Task<ModernizeVerification> VerifyProjectAsync(ModernizeVerifyRequest request, ScratchWorktree scratch, string beforeLog, string project, int index, CancellationToken cancellationToken)
    {
        var binlog = Path.Combine(scratch.Path, ".offramp", string.Create(CultureInfo.InvariantCulture, $"modernize-{index}.binlog"));
        var arguments = new List<string>
        {
            "build", scratch.Resolve(project), "-bl:" + binlog, "-c", request.Config.Verify.Configuration,
            "-nologo", "-v:minimal", "-clp:NoSummary", "-nodeReuse:false", "--no-incremental",
        };
        arguments.AddRange(BuildProperties.Arguments(request.Config.Verify));
        var build = await BuildAsync(request, scratch, arguments, cancellationToken);

        // Restoring the PackageReference way turns NuGet audit on. When known vulnerabilities, made
        // errors by TreatWarningsAsErrors, are all that failed, the conversion is not at fault:
        // report them and verify with audit off.
        if (build.ExitCode != 0 && Errors(build, scratch) is { Count: > 0 } auditErrors && auditErrors.All(IsAudit))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4305,
                $"{project}: the converted project restores its packages the PackageReference way, which turns NuGet audit on, and its warnings are errors: {string.Join(" | ", auditErrors.Take(3))}",
                new DiagnosticLocation(project),
                [KeyValuePair.Create<string, JsonNode?>("errors", new JsonArray([.. auditErrors.Select(e => (JsonNode?)e)]))]);
            build = await BuildAsync(request, scratch, [.. arguments, "-p:NuGetAudit=false"], cancellationToken);
        }

        var frameworks = request.Model.Projects.FirstOrDefault(p => p.Id == project)?.TargetFrameworks ?? [];
        var before = CompileSets.Read(beforeLog, RepoPaths.ToAbsolute(request.RepositoryRoot, project), request.RepositoryRoot, frameworks.Count == 1 ? frameworks[0] : "");
        var after = File.Exists(binlog) ? CompileSets.Read(binlog, scratch.Resolve(project), scratch.Path) : [];
        var errors = build.ExitCode == 0 ? [] : Errors(build, scratch);
        var codes = ErrorCodes(errors);
        var targets = new List<CompileSetDifference>();
        var missing = new List<string>();
        foreach (var (framework, set) in before)
        {
            if (after.TryGetValue(framework, out var converted))
            {
                // What the converted project's restore resolved explains the references PackageReference adds.
                var assets = Path.Combine(Path.GetDirectoryName(scratch.Resolve(project))!, "obj", "project.assets.json");
                targets.Add(CompileSets.Compare(set, converted, request.ConvertedPackages, RestoredPackages.Read(assets, framework)));
            }
            else
            {
                missing.Add(framework);
            }
        }

        var passed = build.ExitCode == 0 && missing.Count == 0 && targets.All(t => t.Identical);
        if (!passed)
        {
            var reasons = new List<string>();
            if (build.ExitCode != 0)
            {
                reasons.Add("the converted project does not build" + (errors.Count > 0 ? $" ({Count(errors.Count, codes)}): " + string.Join(" | ", errors.Take(3).Select(Shorten)) : ""));
            }

            if (missing.Count > 0)
            {
                reasons.Add("no compiler call for " + string.Join(", ", missing));
            }

            foreach (var target in targets.Where(t => !t.Identical))
            {
                reasons.Add(Describe(target));
            }

            Fail(request, project, string.Join("; ", reasons));
        }

        return new ModernizeVerification
        {
            Passed = passed,
            Targets = targets,
            AddedTargets = [.. after.Keys.Except(before.Keys, StringComparer.Ordinal)],
            Built = build.ExitCode == 0,
            BuildErrorCount = errors.Count,
            BuildErrorCodes = codes,
            BuildErrors = [.. errors.Take(10).Select(Shorten)],
        };
    }

    /// <summary>
    /// What the scratch copy needs beyond the committed tree: the model's inputs, every compile item, HintPath
    /// assemblies (a packages folder is rarely committed), the converted projects' folders, and every other file
    /// the scan's build read from the working tree (ADR 0062: imports, packages' build files, resources, copy sources).
    /// </summary>
    private static List<string> Files(ModernizeVerifyRequest request)
    {
        var root = request.RepositoryRoot;
        var files = new HashSet<string>(StringComparer.Ordinal);
        files.UnionWith(request.Model.Inputs.Select(i => i.Path));
        if (request.Model.Source.Kind != WorkspaceSourceKind.Complog)
        {
            files.UnionWith(BuildReads.Read(RepoPaths.ToAbsolute(root, request.Model.Source.Path), root, WorkspaceStore.StateDirectory(root, request.Config)));
        }

        foreach (var project in request.Model.Projects)
        {
            files.UnionWith(project.Compile);
            files.UnionWith(project.AssemblyReferences.Select(r => r.HintPath).OfType<string>());
        }

        foreach (var project in request.Projects)
        {
            var directory = RepoPaths.ToAbsolute(root, Path.GetDirectoryName(project) ?? "");
            files.UnionWith(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => RepoPaths.ToRepositoryRelative(root, f))
                .Where(f => !f.Split('/').Any(s => s is "bin" or "obj" or ".offramp" or ".git")));
        }

        return [.. files.Where(f => File.Exists(RepoPaths.ToAbsolute(root, f))).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>OFR4309</c> when the scratch copy took files from the working tree that <c>HEAD</c> does not have: the
    /// verification holds for this working tree, not for a clean checkout. Files of restored packages
    /// (<c>packages/&lt;Id&gt;.&lt;Version&gt;/</c> of a package a packages.config lists) are counted, not named.
    /// </summary>
    private static void ReportUncommitted(ModernizeVerifyRequest request, ScratchWorktree scratch)
    {
        var uncommitted = scratch.Uncommitted;
        var packages = uncommitted.Where(f => request.Model.Projects.Any(p => p.PackagesConfigPackageFor(f) is not null)).ToHashSet(StringComparer.Ordinal);
        var files = uncommitted.Where(f => !packages.Contains(f)).ToList();
        if (files.Count == 0)
        {
            return;
        }

        var shown = string.Join(", ", files.Take(5));
        var more = files.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $", and {files.Count - 5} more") : "";
        var restored = packages.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"; also {packages.Count} file{(packages.Count == 1 ? "" : "s")} of restored packages") : "";
        request.Diagnostics.Report(DiagnosticCatalog.OFR4309,
            string.Create(CultureInfo.InvariantCulture, $"Verification used {files.Count} file{(files.Count == 1 ? "" : "s")} from the working tree that HEAD does not have (untracked or ignored by git): {shown}{more}{restored}."),
            data:
            [
                KeyValuePair.Create<string, JsonNode?>("files", new JsonArray([.. files.Select(f => (JsonNode?)f)])),
                KeyValuePair.Create<string, JsonNode?>("packageFiles", packages.Count),
            ]);
    }

    private static void Apply(ChangeSet changeSet, ScratchWorktree scratch)
    {
        foreach (var edit in changeSet.Edits)
        {
            File.WriteAllBytes(scratch.Resolve(edit.Path), edit.After);
        }

        foreach (var create in changeSet.Creates)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scratch.Resolve(create.Path))!);
            File.WriteAllBytes(scratch.Resolve(create.Path), create.Content);
        }

        foreach (var delete in changeSet.Deletes)
        {
            File.Delete(scratch.Resolve(delete.Path));
        }
    }

    private static string Describe(CompileSetDifference difference)
    {
        var parts = new List<string>();
        void Add(string what, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                parts.Add(what + " " + string.Join(", ", values));
            }
        }

        Add("sources added:", difference.SourcesAdded);
        Add("sources removed:", difference.SourcesRemoved);
        Add("references added:", difference.ReferencesAdded);
        Add("references removed:", difference.ReferencesRemoved);
        Add("resources added:", difference.ResourcesAdded);
        Add("resources removed:", difference.ResourcesRemoved);
        return difference.TargetFramework + ": " + string.Join("; ", parts);
    }

    private static Task<ProcessResult> BuildAsync(ModernizeVerifyRequest request, ScratchWorktree scratch, List<string> arguments, CancellationToken cancellationToken) =>
        request.Processes.RunAsync(new ProcessSpec("dotnet", arguments)
        {
            WorkingDirectory = scratch.Path,
            Timeout = TimeSpan.FromSeconds(request.Config.Verify.TimeoutSeconds),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        }, cancellationToken);

    /// <summary>NU1901–NU1904: a package with a known vulnerability (low to critical).</summary>
    private static readonly string[] AuditCodes = ["NU1901", "NU1902", "NU1903", "NU1904"];

    private static bool IsAudit(string error) =>
        AuditCodes.Any(code => error.Contains(": error " + code + ":", StringComparison.Ordinal));

    /// <summary>Every distinct error line of a build, in order, with the scratch copy's paths made repository-relative.</summary>
    private static List<string> Errors(ProcessResult build, ScratchWorktree scratch)
    {
        var prefixes = new[] { scratch.Path.TrimEnd('/', '\\') + "/", scratch.Path.TrimEnd('/', '\\') + "\\" };
        return [.. (build.StandardOutput + "\n" + build.StandardError).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(l => Portable(prefixes.Aggregate(l, (line, prefix) => line.Replace(prefix, "", StringComparison.Ordinal))))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>An error line with forward slashes in its file (before <c>: error </c>) and its project (<c>[...]</c> at the end), as on every OS.</summary>
    private static string Portable(string error)
    {
        var at = error.IndexOf(": error ", StringComparison.Ordinal);
        var tail = error[at..];
        var bracket = tail.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracket >= 0 && tail.EndsWith(']'))
        {
            tail = tail[..bracket] + tail[bracket..].Replace('\\', '/');
        }

        return error[..at].Replace('\\', '/') + tail;
    }

    private static string Shorten(string error) => error.Length > 300 ? error[..300] : error;

    /// <summary>The errors by code (<c>... : error CS0246: ...</c>), the most frequent first.</summary>
    private static List<BuildErrorCode> ErrorCodes(IReadOnlyList<string> errors) =>
        [.. errors.GroupBy(Code, StringComparer.Ordinal)
            .Select(g => new BuildErrorCode(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Code, StringComparer.Ordinal)];

    /// <summary>The code after <c>: error </c>, or <c>other</c> when the message has none.</summary>
    private static string Code(string error)
    {
        var rest = error[(error.IndexOf(": error ", StringComparison.Ordinal) + ": error ".Length)..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        var code = colon > 0 ? rest[..colon].Trim() : "";
        return code.Length > 0 && code.All(char.IsAsciiLetterOrDigit) ? code : "other";
    }

    /// <summary>"71 errors: 21 CS0246, 8 CS0234, 6 CS0012, and 4 more codes".</summary>
    private static string Count(int count, List<BuildErrorCode> codes)
    {
        var shown = string.Join(", ", codes.Take(5).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Count} {c.Code}")));
        var more = codes.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $", and {codes.Count - 5} more code{(codes.Count - 5 == 1 ? "" : "s")}") : "";
        return string.Create(CultureInfo.InvariantCulture, $"{count} error{(count == 1 ? "" : "s")}: {shown}{more}");
    }

    private static void Fail(ModernizeVerifyRequest request, string project, string message) =>
        request.Diagnostics.Report(DiagnosticCatalog.OFR4303, $"{project}: {message}", new DiagnosticLocation(project));
}
