using System.Globalization;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Moves;

public sealed record MoveExecution
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    /// <summary>Whether to verify after moving (<c>move.verify</c>, or <c>--verify</c>): anything but <c>none</c> verifies once at the end.</summary>
    public required string VerifyPolicy { get; init; }

    public required IGitService Git { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    public required DateTimeOffset Now { get; init; }
}

/// <summary>The applied move; <see cref="Partial"/> when a failed verification left it in place (<c>verify.onFailure: keep</c>).</summary>
public sealed record MoveOutcome(MoveTestsResult Result, bool Partial);

/// <summary>
/// Applies a planned move (docs/spec/commands/move.md#move-apply): the change set through a
/// journal, then verification of the projects it touched; a failed verification rolls the
/// move back from the journal (<c>OFR2050</c>) unless <c>verify.onFailure</c> is <c>keep</c>.
/// </summary>
public static class MoveExecutor
{
    public const string Command = "move tests";

    public static async Task<MoveOutcome> ApplyAsync(MoveTestsPlan plan, MoveExecution execution, CancellationToken cancellationToken)
    {
        var result = plan.Result;
        if (plan.ChangeSet is null || plan.ChangeSet.IsEmpty)
        {
            return new MoveOutcome(result, false);
        }

        var applier = new ChangeSetApplier(execution.RepositoryRoot, execution.Git);
        string journal;
        using (execution.Progress.BeginPhase("Applying the move", 1, 2))
        {
            journal = await applier.ApplyAsync(plan.ChangeSet, Command, execution.Now, cancellationToken);
        }

        result = result with { Applied = true, Journal = journal };
        if (string.Equals(execution.VerifyPolicy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new MoveOutcome(result, false);
        }

        var verification = await VerifyAsync(result, execution, cancellationToken);
        result = result with { Verify = verification };
        if (verification is null || verification.Passed)
        {
            return new MoveOutcome(result, false);
        }

        if (string.Equals(execution.Config.Verify.OnFailure, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new MoveOutcome(result, true);
        }

        await applier.RollbackAsync(journal, cancellationToken);
        execution.Diagnostics.Report(DiagnosticCatalog.OFR2050,
            string.Create(CultureInfo.InvariantCulture, $"Verification failed after moving {result.Moves.Count} file{(result.Moves.Count == 1 ? "" : "s")}; the move was rolled back from {journal}."),
            new DiagnosticLocation(result.Project));
        return new MoveOutcome(result with { Applied = false, RolledBack = true }, false);
    }

    /// <summary>Builds the source, the destination, and the source's direct dependents.</summary>
    private static Task<VerifyResult?> VerifyAsync(MoveTestsResult result, MoveExecution execution, CancellationToken cancellationToken)
    {
        var projects = VerifySelector.Affected(execution.Model, [result.Project])
            .Append(result.To!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return VerifyRunner.RunAsync(new VerifyRequest
        {
            RepositoryRoot = execution.RepositoryRoot,
            Model = execution.Model,
            Config = execution.Config.Verify,
            Mode = Enum.Parse<VerifyMode>(execution.Config.Verify.Mode, ignoreCase: true),
            Projects = projects,
            Everything = false,
            Scope = string.Create(CultureInfo.InvariantCulture, $"{projects.Count} projects touched by the move and the source's direct dependents"),
            TargetFramework = execution.Config.TargetFramework,
            Processes = execution.Processes,
            Diagnostics = execution.Diagnostics,
            Progress = execution.Progress,
        }, cancellationToken);
    }
}
