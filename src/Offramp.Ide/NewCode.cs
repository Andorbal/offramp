using System.Collections.Concurrent;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Output;

namespace Offramp.Ide;

/// <summary>
/// Which lines are new (docs/spec/commands/ide.md#new-code): those that differ from the file at
/// the merge base of <c>HEAD</c> and the configured ref. Outside git every line is new.
/// </summary>
public sealed class NewCode
{
    public const string Lines = "lines";
    public const string Files = "files";
    public const string All = "all";

    private readonly string _root;
    private readonly IGitService _git;
    private readonly bool _everything;
    private readonly ConcurrentDictionary<string, string?> _baseText = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, GitChange>? _changes;

    private NewCode(string root, IGitService git, IdeBase @base, bool everything)
    {
        _root = root;
        _git = git;
        Base = @base;
        _everything = everything;
    }

    public IdeBase Base { get; }

    public static async Task<NewCode> CreateAsync(string repositoryRoot, IGitService git, string? baseRef, string? scope, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        var wanted = string.IsNullOrWhiteSpace(baseRef) ? "auto" : baseRef.Trim();
        var effectiveScope = scope is Lines or Files or All ? scope : Lines;
        if (await git.FindRepositoryRootAsync(repositoryRoot, cancellationToken) is null)
        {
            diagnostics.Report(DiagnosticCatalog.OFR6007, "The repository is not a git repository, so every line counts as new.");
            return new NewCode(repositoryRoot, git, new IdeBase(wanted, null, effectiveScope), everything: true);
        }

        var name = wanted;
        if (wanted == "auto")
        {
            name = await git.ResolveCommitAsync(repositoryRoot, "origin/HEAD", cancellationToken) is null ? "HEAD" : "origin/HEAD";
        }

        var target = await git.ResolveCommitAsync(repositoryRoot, name, cancellationToken);
        if (target is null && name != "HEAD")
        {
            diagnostics.Report(DiagnosticCatalog.OFR6007, $"'{name}' does not name a commit, so new code is compared with HEAD.");
            name = "HEAD";
            target = await git.ResolveCommitAsync(repositoryRoot, name, cancellationToken);
        }

        if (target is null)
        {
            diagnostics.Report(DiagnosticCatalog.OFR6007, "The repository has no commits yet, so every line counts as new.");
            return new NewCode(repositoryRoot, git, new IdeBase(name, null, effectiveScope), everything: true);
        }

        var commit = name == "HEAD" ? target : await git.MergeBaseAsync(repositoryRoot, "HEAD", target, cancellationToken) ?? target;
        return new NewCode(repositoryRoot, git, new IdeBase(name, commit, effectiveScope), everything: false);
    }

    /// <summary>The files that differ from the base (tracked or untracked), repository-relative and sorted; empty outside git.</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(CancellationToken cancellationToken) =>
        _everything || Base.Commit is null ? [] : [.. (await ChangesAsync(cancellationToken)).Keys.Order(StringComparer.Ordinal)];

    /// <summary>The new lines (1-based, sorted) of a file whose current text is <paramref name="text"/>.</summary>
    public async Task<IReadOnlyList<int>> NewLinesAsync(string file, string text, CancellationToken cancellationToken)
    {
        var count = UnifiedDiff.SplitLines(text).Count;
        if (_everything || Base.Scope == All || Base.Commit is null)
        {
            return [.. Enumerable.Range(1, count)];
        }

        var changes = await ChangesAsync(cancellationToken);
        var original = changes.TryGetValue(file, out var change) ? change.OriginalPath ?? file : file;
        var baseText = await BaseTextAsync(original, cancellationToken);
        if (baseText is null)
        {
            return [.. Enumerable.Range(1, count)];
        }

        var changed = UnifiedDiff.ChangedLines(baseText, text);
        return Base.Scope == Files && changed.Count > 0 ? [.. Enumerable.Range(1, count)] : changed;
    }

    /// <summary>Consecutive lines as inclusive <c>[start, end]</c> ranges.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Ranges(IReadOnlyList<int> lines)
    {
        var ranges = new List<IReadOnlyList<int>>();
        var i = 0;
        while (i < lines.Count)
        {
            var start = lines[i];
            var end = start;
            while (i + 1 < lines.Count && lines[i + 1] == end + 1)
            {
                end = lines[++i];
            }

            ranges.Add([start, end]);
            i++;
        }

        return ranges;
    }

    private async Task<IReadOnlyDictionary<string, GitChange>> ChangesAsync(CancellationToken cancellationToken) =>
        _changes ??= (await _git.ChangesSinceAsync(_root, Base.Commit!, cancellationToken)).ToDictionary(c => c.Path, StringComparer.Ordinal);

    private async Task<string?> BaseTextAsync(string path, CancellationToken cancellationToken)
    {
        if (_baseText.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var text = await _git.ShowFileAsync(_root, Base.Commit!, path, cancellationToken);
        _baseText[path] = text;
        return text;
    }
}
