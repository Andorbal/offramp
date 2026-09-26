using System.Globalization;
using System.Text.Json;
using Offramp.Core.Caching;
using Offramp.Core.Git;
using Offramp.Core.Json;
using Offramp.Core.Paths;

namespace Offramp.Refactoring.ChangeSets;

/// <summary>A moved file whose bytes changed: a bug, never a user error.</summary>
public sealed class PurityViolationException(string message) : Exception(message);

/// <summary>Files changed after the change set was applied; rolling back would lose those changes.</summary>
public sealed class RollbackConflictException(IReadOnlyList<string> paths)
    : Exception("Changed since the move: " + string.Join(", ", paths))
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>A journal step cannot be performed: its file is neither as the step expects nor as the step leaves it (changed since it was planned).</summary>
public sealed class JournalConflictException(IReadOnlyList<string> paths)
    : Exception("Neither as planned nor as moved: " + string.Join(", ", paths))
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>
/// Applies a change set through a journal (docs/spec/commands/move.md#move-apply): each
/// step is written to the journal before it is performed. New files and edits come first,
/// then renames with <c>git mv</c> (a plain move outside git), each checked to leave the
/// file's bytes unchanged. Rollback undoes the completed steps in reverse.
/// </summary>
public sealed class ChangeSetApplier(string repositoryRoot, IGitService git)
{
    public const string JournalDirectory = ".offramp/journal";

    /// <summary>Applies the change set and returns the journal's repository-relative path. Renames run in source-path order.</summary>
    public async Task<string> ApplyAsync(ChangeSet changeSet, string command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        changeSet.Renames.Sort((a, b) => string.CompareOrdinal(a.From, b.From));
        var journalPath = Begin(changeSet, command, now);
        await ContinueAsync(journalPath, int.MaxValue, cancellationToken);
        return journalPath;
    }

    /// <summary>
    /// Writes the journal of a change set without performing anything: new files and edits
    /// (in path order), then renames in the change set's order. Every step carries what it
    /// writes, so <see cref="ContinueAsync"/> can finish the journal in a later process.
    /// </summary>
    /// <param name="changeSet">The change set.</param>
    /// <param name="command">The command applying it (<c>move apply</c>).</param>
    /// <param name="now">The time the journal is named after.</param>
    /// <param name="plan">The plan file being applied, repository-relative; <c>--resume</c> finds the journal by it.</param>
    public string Begin(ChangeSet changeSet, string command, DateTimeOffset now, string? plan = null)
    {
        var steps = new List<JournalStep>();
        steps.AddRange(changeSet.Creates.OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => new JournalStep { Kind = JournalStepKind.Create, Path = c.Path, After = Convert.ToBase64String(c.Content), Sha256 = ContentHash.Sha256(c.Content) }));
        steps.AddRange(changeSet.Edits.OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => new JournalStep
            {
                Kind = JournalStepKind.Edit, Path = e.Path, Before = Convert.ToBase64String(e.Before), After = Convert.ToBase64String(e.After), Sha256 = ContentHash.Sha256(e.After),
            }));
        steps.AddRange(changeSet.Renames.Select(r => new JournalStep { Kind = JournalStepKind.Rename, Path = r.To, From = r.From, Sha256 = r.Sha256 }));

        var stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var journalPath = Unique($"{JournalDirectory}/{stamp}-{command.Replace(' ', '-')}");
        Save(journalPath, new Journal
        {
            Command = command,
            CreatedAt = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Plan = plan,
            State = JournalState.Applying,
            Steps = steps,
        });
        return journalPath;
    }

    /// <summary>
    /// Performs up to <paramref name="maxSteps"/> pending steps of a journal, in order, marking
    /// each done; the journal becomes applied when none remain. A step an interrupted run
    /// already performed is recognized by its result and only marked done. Returns the number
    /// of steps still pending.
    /// </summary>
    /// <exception cref="JournalConflictException">A file is neither as the step expects nor as it leaves it.</exception>
    public async Task<int> ContinueAsync(string journalPath, int maxSteps, CancellationToken cancellationToken)
    {
        var journal = Read(journalPath);
        var steps = journal.Steps.ToList();
        var performed = 0;
        for (var i = 0; i < steps.Count && performed < maxSteps; i++)
        {
            if (steps[i].Done)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var created = await PerformAsync(steps[i], cancellationToken);
            steps[i] = steps[i] with { Done = true, CreatedDirectories = created };
            performed++;
            Save(journalPath, journal with { Steps = steps });
        }

        var pending = steps.Count(s => !s.Done);
        if (pending == 0)
        {
            Save(journalPath, journal with { Steps = steps, State = JournalState.Applied });
        }

        return pending;
    }

    /// <summary>
    /// Undoes every completed step of a journal, newest first, and marks it rolled back. Nothing
    /// is undone when a file the steps wrote has changed since (<see cref="RollbackConflictException"/>).
    /// </summary>
    public async Task RollbackAsync(string journalPath, CancellationToken cancellationToken)
    {
        var journal = Read(journalPath);
        var changed = journal.Steps
            .Where(s => s.Done && s.Sha256 is not null)
            .Where(s => !File.Exists(Absolute(s.Path)) || ContentHash.Sha256File(Absolute(s.Path)) != s.Sha256)
            .Select(s => s.Path)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (changed.Count > 0)
        {
            throw new RollbackConflictException(changed);
        }

        foreach (var step in journal.Steps.Where(s => s.Done).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UndoAsync(step, cancellationToken);
        }

        Save(journalPath, journal with { State = JournalState.RolledBack, Steps = [.. journal.Steps.Select(s => s with { Done = false })] });
    }

    public Journal Read(string journalPath) =>
        JsonSerializer.Deserialize(File.ReadAllText(Absolute(journalPath)), RefactoringJsonContext.Default.Journal)
            ?? throw new InvalidDataException($"{journalPath} is empty.");

    private async Task<IReadOnlyList<string>> PerformAsync(JournalStep step, CancellationToken cancellationToken)
    {
        var path = Absolute(step.Path);
        var current = File.Exists(path) ? ContentHash.Sha256File(path) : null;
        switch (step.Kind)
        {
            case JournalStepKind.Create:
            {
                if (current == step.Sha256)
                {
                    return [];
                }

                if (current is not null)
                {
                    throw new JournalConflictException([step.Path]);
                }

                var created = MissingDirectories(step.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(step.After!), cancellationToken);
                return created;
            }

            case JournalStepKind.Edit:
            {
                if (current == step.Sha256)
                {
                    return [];
                }

                var before = Convert.FromBase64String(step.Before!);
                if (current is null || !File.ReadAllBytes(path).AsSpan().SequenceEqual(before))
                {
                    throw new JournalConflictException([step.Path]);
                }

                await File.WriteAllBytesAsync(path, Convert.FromBase64String(step.After!), cancellationToken);
                return [];
            }

            default:
            {
                var from = Absolute(step.From!);
                if (!File.Exists(from) && current == step.Sha256)
                {
                    return [];
                }

                if (!File.Exists(from) || current is not null)
                {
                    throw new JournalConflictException([step.From!, step.Path]);
                }

                if (ContentHash.Sha256File(from) != step.Sha256)
                {
                    throw new JournalConflictException([step.From!]);
                }

                var created = MissingDirectories(step.Path);
                await git.MoveAsync(repositoryRoot, from, path, cancellationToken);
                if (ContentHash.Sha256File(path) != step.Sha256)
                {
                    throw new PurityViolationException($"Moving {step.From} to {step.Path} changed its bytes.");
                }

                return created;
            }
        }
    }

    private async Task UndoAsync(JournalStep step, CancellationToken cancellationToken)
    {
        switch (step.Kind)
        {
            case JournalStepKind.Create:
                File.Delete(Absolute(step.Path));
                break;
            case JournalStepKind.Edit:
                await File.WriteAllBytesAsync(Absolute(step.Path), Convert.FromBase64String(step.Before!), cancellationToken);
                break;
            default:
                await git.MoveAsync(repositoryRoot, Absolute(step.Path), Absolute(step.From!), cancellationToken);
                break;
        }

        foreach (var directory in step.CreatedDirectories)
        {
            var path = Absolute(directory);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
    }

    /// <summary>The ancestors of a file that do not exist yet, deepest first.</summary>
    private List<string> MissingDirectories(string file)
    {
        var result = new List<string>();
        var directory = Path.GetDirectoryName(file.Replace('\\', '/'))?.Replace('\\', '/');
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(Absolute(directory)))
        {
            result.Add(directory);
            directory = Path.GetDirectoryName(directory)?.Replace('\\', '/');
        }

        return result;
    }

    private string Unique(string stem)
    {
        var candidate = stem + ".json";
        for (var n = 2; File.Exists(Absolute(candidate)); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.json");
        }

        return candidate;
    }

    private void Save(string journalPath, Journal journal)
    {
        var path = Absolute(journalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OfframpJson.Serialize(journal, RefactoringJsonContext.Default.Journal), new System.Text.UTF8Encoding(false));
    }

    private string Absolute(string relative) => RepoPaths.ToAbsolute(repositoryRoot, relative);
}
