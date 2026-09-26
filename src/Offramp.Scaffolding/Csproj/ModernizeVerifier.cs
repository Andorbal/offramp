using System.Globalization;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
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
        arguments.AddRange(request.Config.Verify.Properties.Select(p => $"-p:{p.Key}={p.Value}"));
        var build = await request.Processes.RunAsync(new ProcessSpec("dotnet", arguments)
        {
            WorkingDirectory = scratch.Path,
            Timeout = TimeSpan.FromSeconds(request.Config.Verify.TimeoutSeconds),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        }, cancellationToken);

        var frameworks = request.Model.Projects.FirstOrDefault(p => p.Id == project)?.TargetFrameworks ?? [];
        var before = CompileSets.Read(beforeLog, RepoPaths.ToAbsolute(request.RepositoryRoot, project), request.RepositoryRoot, frameworks.Count == 1 ? frameworks[0] : "");
        var after = File.Exists(binlog) ? CompileSets.Read(binlog, scratch.Resolve(project), scratch.Path) : [];
        var errors = build.ExitCode == 0 ? [] : Errors(build);
        var targets = new List<CompileSetDifference>();
        var missing = new List<string>();
        foreach (var (framework, set) in before)
        {
            if (after.TryGetValue(framework, out var converted))
            {
                targets.Add(CompileSets.Compare(set, converted, request.ConvertedPackages));
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
                reasons.Add("the converted project does not build" + (errors.Count > 0 ? ": " + string.Join(" | ", errors.Take(3)) : ""));
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
            BuildErrors = errors,
        };
    }

    /// <summary>What the scratch copy needs beyond the committed tree: the model's inputs, every compile item, HintPath assemblies (a packages folder is rarely committed), and the converted projects' folders.</summary>
    private static List<string> Files(ModernizeVerifyRequest request)
    {
        var root = request.RepositoryRoot;
        var files = new HashSet<string>(StringComparer.Ordinal);
        files.UnionWith(request.Model.Inputs.Select(i => i.Path));
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

    private static List<string> Errors(ProcessResult build) =>
        [.. (build.StandardOutput + "\n" + build.StandardError).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(l => l.Length > 300 ? l[..300] : l)
            .Distinct(StringComparer.Ordinal)
            .Take(10)];

    private static void Fail(ModernizeVerifyRequest request, string project, string message) =>
        request.Diagnostics.Report(DiagnosticCatalog.OFR4303, $"{project}: {message}", new DiagnosticLocation(project));
}
