using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Moves;

public sealed record MoveApplyRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required MovePlanDocument Plan { get; init; }

    /// <summary>The plan file, repository-relative; the journal records it for <c>--resume</c>.</summary>
    public required string PlanPath { get; init; }

    /// <summary>The hash of the current workspace model, compared with the plan's.</summary>
    public required string WorkspaceHash { get; init; }

    /// <summary><c>none</c>, <c>per-project</c>, <c>batch:N</c>, or <c>end</c>.</summary>
    public required string VerifyPolicy { get; init; }

    /// <summary><c>rollback</c> or <c>keep</c>.</summary>
    public required string OnFailure { get; init; }

    public bool Resume { get; init; }

    /// <summary>Apply a plan made from a different workspace model.</summary>
    public bool Force { get; init; }

    public required IGitService Git { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    public required DateTimeOffset Now { get; init; }

    /// <summary>Project files the plan creates (<c>move extract</c>), by path: their content before the move's edits.</summary>
    public IReadOnlyDictionary<string, byte[]>? Created { get; init; }
}

/// <summary>How <c>move apply</c> ended.</summary>
public enum MoveApplyStatus
{
    /// <summary>Everything planned was applied (and verified, when asked).</summary>
    Applied,

    /// <summary>Some moves were skipped, or a failed verification left the tree as it was (<c>keep</c>): exit 4.</summary>
    Partial,

    /// <summary>Nothing was applied: the plan is from another workspace model (<c>OFR0002</c>), exit 3.</summary>
    Stale,

    /// <summary>Nothing to resume (<c>OFR2152</c>), exit 2.</summary>
    NothingToResume,

    /// <summary>Verification failed and the run was rolled back, or the journal could not be finished.</summary>
    Failed,
}

public sealed record MoveApplyOutcome(MoveApplyResult Result, MoveApplyStatus Status);

/// <summary>
/// Applies a move plan (docs/spec/commands/move.md#move-apply): checks that the workspace model
/// and every planned file are unchanged, writes the journal, performs project edits and then
/// renames, and verifies per the policy. Batches never separate a file from the files it needs,
/// so every verified state compiles if the plan is right. A failed verification rolls the
/// whole run back (<c>OFR2050</c>) unless the policy is to keep it.
/// </summary>
public static class MoveApplier
{
    public const string Command = "move apply";

    public static async Task<MoveApplyOutcome> ApplyAsync(MoveApplyRequest request, CancellationToken cancellationToken)
    {
        if (request.Resume)
        {
            return await ResumeAsync(request, cancellationToken);
        }

        var plan = request.Plan;
        var empty = new MoveApplyResult { Plan = request.PlanPath, Applied = false, Moved = [], Skipped = [], Edited = [] };
        if (!string.Equals(plan.WorkspaceHash, request.WorkspaceHash, StringComparison.Ordinal))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0002,
                $"{request.PlanPath} was made from a different workspace model{(request.Force ? "; applying it anyway (--force)" : "; plan again, or pass --force")}.",
                data: [KeyValuePair.Create<string, JsonNode?>("plan", request.PlanPath)]);
            if (!request.Force)
            {
                return new MoveApplyOutcome(empty, MoveApplyStatus.Stale);
            }
        }

        var skipped = Changed(request);
        var moves = plan.Moves.Where(m => !skipped.Contains(m.File)).ToList();
        if (moves.Count == 0)
        {
            return new MoveApplyOutcome(empty with { Skipped = [.. skipped] }, skipped.Count > 0 ? MoveApplyStatus.Partial : MoveApplyStatus.Applied);
        }

        var changeSet = MoveChangeSet.Build(request.RepositoryRoot, plan, skipped, out var edited, request.Model, request.Created);
        await AddToSolutionsAsync(request.RepositoryRoot, plan, changeSet, cancellationToken);
        edited = [.. changeSet.Edits.Select(e => e.Path).Order(StringComparer.Ordinal)];
        var batches = Batches(moves, BatchSize(request.VerifyPolicy));
        var order = batches.SelectMany(b => b).Select((m, i) => (m.File, i)).ToDictionary(x => x.File, x => x.i, StringComparer.Ordinal);
        changeSet.Renames.Sort((a, b) => order[a.From].CompareTo(order[b.From]));

        var applier = new ChangeSetApplier(request.RepositoryRoot, request.Git);
        var journal = applier.Begin(changeSet, Command, request.Now, request.PlanPath);
        var result = empty with { Journal = journal, Skipped = [.. skipped], Edited = edited };
        var verifications = new List<VerifyResult>();
        var moved = new List<string>();
        var first = changeSet.Creates.Count + changeSet.Edits.Count;
        for (var b = 0; b < batches.Count; b++)
        {
            using (request.Progress.BeginPhase(batches.Count == 1 ? "Applying the move" : $"Applying batch {b + 1} of {batches.Count}", b + 1, batches.Count))
            {
                await applier.ContinueAsync(journal, (b == 0 ? first : 0) + batches[b].Count, cancellationToken);
            }

            moved.AddRange(batches[b].Select(m => m.File));
            if (batches.Count == 1 || BatchSize(request.VerifyPolicy) is null)
            {
                continue;
            }

            if (await VerifyAsync(request, verifications, $"batch {b + 1} of {batches.Count}", cancellationToken) is false)
            {
                return await FailedAsync(request, applier, result with { Moved = [.. moved.Order(StringComparer.Ordinal)], Verifications = verifications }, cancellationToken);
            }
        }

        result = result with { Applied = true, Moved = [.. moved.Order(StringComparer.Ordinal)] };
        if (BatchSize(request.VerifyPolicy) is null || batches.Count == 1)
        {
            if (await VerifyAsync(request, verifications, null, cancellationToken) is false)
            {
                return await FailedAsync(request, applier, result with { Verifications = verifications }, cancellationToken);
            }
        }

        return new MoveApplyOutcome(result with { Verifications = verifications }, skipped.Count > 0 ? MoveApplyStatus.Partial : MoveApplyStatus.Applied);
    }

    /// <summary>Finishes the newest interrupted journal of this plan, then verifies once.</summary>
    private static async Task<MoveApplyOutcome> ResumeAsync(MoveApplyRequest request, CancellationToken cancellationToken)
    {
        var applier = new ChangeSetApplier(request.RepositoryRoot, request.Git);
        var empty = new MoveApplyResult { Plan = request.PlanPath, Applied = false, Moved = [], Skipped = [], Edited = [], Resumed = true };
        var journal = Interrupted(request, applier);
        if (journal is null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR2152, $"No interrupted `move apply` journal applies {request.PlanPath}.",
                data: [KeyValuePair.Create<string, JsonNode?>("plan", request.PlanPath)]);
            return new MoveApplyOutcome(empty, MoveApplyStatus.NothingToResume);
        }

        var steps = applier.Read(journal).Steps;
        var result = empty with
        {
            Journal = journal,
            Moved = [.. steps.Where(s => s.Kind == JournalStepKind.Rename).Select(s => s.From!).Order(StringComparer.Ordinal)],
            Edited = [.. steps.Where(s => s.Kind == JournalStepKind.Edit).Select(s => s.Path).Order(StringComparer.Ordinal)],
        };
        try
        {
            using (request.Progress.BeginPhase("Finishing the interrupted move", 1, 1))
            {
                await applier.ContinueAsync(journal, int.MaxValue, cancellationToken);
            }
        }
        catch (JournalConflictException conflict)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR2152,
                $"{string.Join(", ", conflict.Paths)} {(conflict.Paths.Count == 1 ? "is" : "are")} neither as planned nor as moved; the journal cannot be finished. Roll it back with `offramp move rollback --journal {journal}`.",
                data: [KeyValuePair.Create<string, JsonNode?>("files", new JsonArray([.. conflict.Paths.Select(p => (JsonNode?)p)]))]);
            return new MoveApplyOutcome(result with { Moved = [] }, MoveApplyStatus.Failed);
        }

        result = result with { Applied = true };
        var verifications = new List<VerifyResult>();
        if (await VerifyAsync(request with { VerifyPolicy = BatchSize(request.VerifyPolicy) is null ? request.VerifyPolicy : "end" }, verifications, null, cancellationToken) is false)
        {
            return await FailedAsync(request, applier, result with { Verifications = verifications }, cancellationToken);
        }

        return new MoveApplyOutcome(result with { Verifications = verifications }, MoveApplyStatus.Applied);
    }

    private static async Task<MoveApplyOutcome> FailedAsync(MoveApplyRequest request, ChangeSetApplier applier, MoveApplyResult result, CancellationToken cancellationToken)
    {
        if (string.Equals(request.OnFailure, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new MoveApplyOutcome(result with { Applied = true }, MoveApplyStatus.Partial);
        }

        try
        {
            await applier.RollbackAsync(result.Journal!, cancellationToken);
        }
        catch (RollbackConflictException conflict)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR2151,
                $"{string.Join(", ", conflict.Paths)} changed during verification; the move was left in place.",
                data: [KeyValuePair.Create<string, JsonNode?>("files", new JsonArray([.. conflict.Paths.Select(p => (JsonNode?)p)]))]);
            return new MoveApplyOutcome(result with { Applied = true }, MoveApplyStatus.Failed);
        }

        request.Diagnostics.Report(DiagnosticCatalog.OFR2050,
            string.Create(CultureInfo.InvariantCulture, $"Verification failed after moving {result.Moved.Count} file{(result.Moved.Count == 1 ? "" : "s")}; the move was rolled back from {result.Journal}."),
            new DiagnosticLocation(request.Plan.From));
        return new MoveApplyOutcome(result with { Applied = false, RolledBack = true, Moved = [] }, MoveApplyStatus.Failed);
    }

    /// <summary>
    /// Planned files that changed since the plan (<c>OFR2150</c>): a different hash, a missing
    /// file, or an occupied destination; and, transitively, the moves that need them.
    /// </summary>
    private static SortedSet<string> Changed(MoveApplyRequest request)
    {
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var move in request.Plan.Moves)
        {
            var path = RepoPaths.ToAbsolute(request.RepositoryRoot, move.File);
            var reason = !File.Exists(path) ? "no longer exists"
                : ContentHash.Sha256File(path) != move.Sha256 ? "changed since the plan"
                : File.Exists(RepoPaths.ToAbsolute(request.RepositoryRoot, move.To)) ? $"cannot move: {move.To} exists"
                : null;
            if (reason is not null)
            {
                skipped.Add(move.File);
                request.Diagnostics.Report(DiagnosticCatalog.OFR2150, $"{move.File} {reason}; it stays.", new DiagnosticLocation(request.Plan.From, move.File));
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var move in request.Plan.Moves.Where(m => !skipped.Contains(m.File)))
            {
                if (move.Needs.FirstOrDefault(skipped.Contains) is { } needed)
                {
                    skipped.Add(move.File);
                    request.Diagnostics.Report(DiagnosticCatalog.OFR2150, $"{move.File} needs {needed}, which stays; it stays too.", new DiagnosticLocation(request.Plan.From, move.File));
                    changed = true;
                }
            }
        }

        return skipped;
    }

    /// <summary>The newest journal of this plan that is still being applied, or null.</summary>
    private static string? Interrupted(MoveApplyRequest request, ChangeSetApplier applier)
    {
        var directory = RepoPaths.ToAbsolute(request.RepositoryRoot, ChangeSetApplier.JournalDirectory);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, "*-move-apply*.json")
            .Select(f => ChangeSetApplier.JournalDirectory + "/" + Path.GetFileName(f))
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault(f => applier.Read(f) is { State: JournalState.Applying } journal && journal.Plan == request.PlanPath);
    }

    /// <summary>N for <c>batch:N</c>; null for the other policies.</summary>
    internal static int? BatchSize(string policy) =>
        policy.StartsWith("batch:", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(policy.AsSpan("batch:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0
            ? size
            : null;

    /// <summary>The plan's <c>addToSolution</c> edits (a project it creates), as edits of the solution files.</summary>
    public static async Task AddToSolutionsAsync(string root, MovePlanDocument plan, ChangeSet changeSet, CancellationToken cancellationToken)
    {
        foreach (var edit in plan.ProjectEdits.Where(e => e.Kind == ProjectEditKind.AddToSolution))
        {
            foreach (var (path, before, after) in await SolutionEditor.AddProjectAsync(root, edit.Project, edit.Value!, cancellationToken))
            {
                if (!before.AsSpan().SequenceEqual(after))
                {
                    changeSet.Edit(path, before, after);
                }
            }
        }
    }

    /// <summary>True when <paramref name="policy"/> is one <c>move apply</c> understands.</summary>
    public static bool IsPolicy(string policy) =>
        policy is "none" or "end" or "per-project" || BatchSize(policy) is not null;

    /// <summary>
    /// Splits moves into batches of about <paramref name="size"/> files (all in one when null).
    /// Files that need each other share a batch, and a file never comes before one it needs.
    /// </summary>
    internal static List<List<PlannedMove>> Batches(IReadOnlyList<PlannedMove> moves, int? size)
    {
        if (size is null)
        {
            return [[.. moves]];
        }

        var batches = new List<List<PlannedMove>>();
        var current = new List<PlannedMove>();
        foreach (var group in DependencyOrder(moves))
        {
            if (current.Count > 0 && current.Count + group.Count > size)
            {
                batches.Add(current);
                current = [];
            }

            current.AddRange(group);
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /// <summary>
    /// Strongly connected groups of moves under <see cref="PlannedMove.Needs"/> (Tarjan's
    /// algorithm, iterative), each after the groups it needs; files and edges in path order.
    /// </summary>
    private static List<List<PlannedMove>> DependencyOrder(IReadOnlyList<PlannedMove> moves)
    {
        var byFile = moves.ToDictionary(m => m.File, StringComparer.Ordinal);
        var files = byFile.Keys.Order(StringComparer.Ordinal).ToList();
        var edges = files.ToDictionary(f => f, f => byFile[f].Needs.Where(byFile.ContainsKey).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var groups = new List<List<PlannedMove>>();
        var next = 0;
        foreach (var start in files.Where(f => !index.ContainsKey(f)))
        {
            var work = new Stack<(string File, int Edge)>();
            work.Push((start, 0));
            index[start] = low[start] = next++;
            stack.Push(start);
            onStack.Add(start);
            while (work.Count > 0)
            {
                var (file, edge) = work.Pop();
                if (edge < edges[file].Count)
                {
                    work.Push((file, edge + 1));
                    var needed = edges[file][edge];
                    if (!index.TryGetValue(needed, out var neededIndex))
                    {
                        index[needed] = low[needed] = next++;
                        stack.Push(needed);
                        onStack.Add(needed);
                        work.Push((needed, 0));
                    }
                    else if (onStack.Contains(needed))
                    {
                        low[file] = Math.Min(low[file], neededIndex);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().File;
                    low[parent] = Math.Min(low[parent], low[file]);
                }

                if (low[file] == index[file])
                {
                    var group = new List<PlannedMove>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        group.Add(byFile[member]);
                    }
                    while (member != file);

                    groups.Add([.. group.OrderBy(m => m.File, StringComparer.Ordinal)]);
                }
            }
        }

        return groups;
    }

    /// <summary>Verifies per the policy; null when the policy is <c>none</c>, else whether every run passed.</summary>
    private static async Task<bool?> VerifyAsync(MoveApplyRequest request, List<VerifyResult> results, string? batch, CancellationToken cancellationToken)
    {
        if (string.Equals(request.VerifyPolicy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projects = Touched(request);
        var runs = string.Equals(request.VerifyPolicy, "per-project", StringComparison.OrdinalIgnoreCase)
            ? projects.Select(p => (IReadOnlyList<string>)[p]).ToList()
            : [projects];
        foreach (var run in runs)
        {
            var scope = run.Count == 1 ? run[0]
                : string.Create(CultureInfo.InvariantCulture, $"{run.Count} projects touched by the move and their direct dependents");
            var result = await VerifyRunner.RunAsync(new VerifyRequest
            {
                RepositoryRoot = request.RepositoryRoot,
                Model = request.Model,
                Config = request.Config.Verify,
                Mode = Enum.Parse<VerifyMode>(request.Config.Verify.Mode, ignoreCase: true),
                Projects = run,
                Everything = false,
                Scope = batch is null ? scope : $"{scope}, after {batch}",
                TargetFramework = request.Config.TargetFramework,
                Processes = request.Processes,
                Diagnostics = request.Diagnostics,
                Progress = request.Progress,
            }, cancellationToken);
            if (result is null)
            {
                continue;
            }

            results.Add(result);
            if (!result.Passed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The source, the destination, and their direct dependents, dependencies first.</summary>
    private static List<string> Touched(MoveApplyRequest request)
    {
        var order = request.Model.Graph.TopologicalOrder.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.Ordinal);
        return VerifySelector.Affected(request.Model, [request.Plan.From, request.Plan.To])
            .Append(request.Plan.From)
            .Append(request.Plan.To)
            .Where(p => request.Model.Projects.Any(m => m.Id == p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => order.GetValueOrDefault(p, int.MaxValue))
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
    }
}
