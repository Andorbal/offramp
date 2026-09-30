using System.Globalization;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Dependencies.Resolution;

/// <summary>The applied resolution; <see cref="Partial"/> when a failed verification left it in place (<c>verify.onFailure: keep</c>).</summary>
public sealed record DllResolveOutcome(ResolveDllsResult Result, bool Partial);

/// <summary>
/// Applies <c>deps resolve-dlls</c> (docs/spec/commands/deps.md): the project edits through a
/// journal, then the configured verification (a restore and build) of the edited projects and
/// their direct dependents. A failed verification rolls the edits back from the journal
/// (<c>OFR1408</c>) unless <c>verify.onFailure</c> is <c>keep</c>: a package that restores but whose
/// assemblies never reach the compiler must not be left behind (ADR 0042).
/// </summary>
public static class DllResolveExecutor
{
    public const string Command = "deps resolve-dlls";

    public static async Task<DllResolveOutcome> ApplyAsync(ResolveDllsPlan plan, MoveExecution execution, CancellationToken cancellationToken)
    {
        var result = plan.Result with { Preview = null };
        if (plan.ChangeSet is null || plan.ChangeSet.IsEmpty)
        {
            return new DllResolveOutcome(result, false);
        }

        var applier = new ChangeSetApplier(execution.RepositoryRoot, execution.Git);
        string journal;
        using (execution.Progress.BeginPhase("Replacing the references", 1, 2))
        {
            journal = await applier.ApplyAsync(plan.ChangeSet, Command, execution.Now, cancellationToken);
        }

        result = result with { Applied = true, Journal = journal };
        if (string.Equals(execution.VerifyPolicy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new DllResolveOutcome(result, false);
        }

        var projects = VerifySelector.Affected(execution.Model, plan.ChangeSet.Edits.Select(e => e.Path));
        var verification = await VerifyRunner.RunAsync(new VerifyRequest
        {
            RepositoryRoot = execution.RepositoryRoot,
            Model = execution.Model,
            Config = execution.Config.Verify,
            Mode = Enum.Parse<VerifyMode>(execution.Config.Verify.Mode, ignoreCase: true),
            Projects = projects,
            Everything = false,
            Scope = string.Create(CultureInfo.InvariantCulture, $"{projects.Count} project{(projects.Count == 1 ? "" : "s")} whose references changed and their direct dependents"),
            TargetFramework = execution.Config.TargetFramework,
            Processes = execution.Processes,
            Diagnostics = execution.Diagnostics,
            Progress = execution.Progress,
        }, cancellationToken);
        result = result with { Verify = verification };
        if (verification is null || verification.Passed)
        {
            return new DllResolveOutcome(result, false);
        }

        if (string.Equals(execution.Config.Verify.OnFailure, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new DllResolveOutcome(result, true);
        }

        await applier.RollbackAsync(journal, cancellationToken);
        var files = plan.ChangeSet.Edits.Count;
        execution.Diagnostics.Report(DiagnosticCatalog.OFR1408,
            string.Create(CultureInfo.InvariantCulture, $"Verification failed after replacing the references in {files} project file{(files == 1 ? "" : "s")}; every file was restored from {journal}."));
        return new DllResolveOutcome(result with { Applied = false, RolledBack = true }, false);
    }
}
