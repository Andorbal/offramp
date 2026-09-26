using System.Globalization;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Codemods;

/// <summary>The applied codemods; <see cref="Partial"/> when a failed verification left them in place (<c>verify.onFailure: keep</c>).</summary>
public sealed record CodemodOutcome(CodemodRunResult Result, bool Partial);

/// <summary>
/// Applies a codemod plan: the change set through a journal, then a build of the projects
/// whose files changed and their direct dependents. A failed verification rolls the change
/// set back from the journal (<c>OFR4507</c>) unless <c>verify.onFailure</c> is <c>keep</c>.
/// </summary>
public static class CodemodExecutor
{
    public const string Command = "codemod run";

    public static async Task<CodemodOutcome> ApplyAsync(CodemodPlan plan, MoveExecution execution, CancellationToken cancellationToken)
    {
        var result = plan.Result with { Preview = null };
        if (plan.ChangeSet.IsEmpty)
        {
            return new CodemodOutcome(result, false);
        }

        var applier = new ChangeSetApplier(execution.RepositoryRoot, execution.Git);
        string journal;
        using (execution.Progress.BeginPhase("Applying the codemods", 1, 2))
        {
            journal = await applier.ApplyAsync(plan.ChangeSet, Command, execution.Now, cancellationToken);
        }

        result = result with { Applied = true, Journal = journal };
        if (string.Equals(execution.VerifyPolicy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new CodemodOutcome(result, false);
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
            Scope = string.Create(CultureInfo.InvariantCulture, $"{projects.Count} project{(projects.Count == 1 ? "" : "s")} the codemods changed and their direct dependents"),
            TargetFramework = execution.Config.TargetFramework,
            Processes = execution.Processes,
            Diagnostics = execution.Diagnostics,
            Progress = execution.Progress,
        }, cancellationToken);
        result = result with { Verify = verification };
        if (verification is null || verification.Passed)
        {
            return new CodemodOutcome(result, false);
        }

        if (string.Equals(execution.Config.Verify.OnFailure, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new CodemodOutcome(result, true);
        }

        await applier.RollbackAsync(journal, cancellationToken);
        execution.Diagnostics.Report(DiagnosticCatalog.OFR4507,
            string.Create(CultureInfo.InvariantCulture, $"Verification failed after the codemods changed {plan.ChangeSet.Edits.Count} file{(plan.ChangeSet.Edits.Count == 1 ? "" : "s")}; every file was restored from {journal}."));
        return new CodemodOutcome(result with { Applied = false, RolledBack = true }, false);
    }
}
