using System.Text;
using Offramp.Core.Caching;

namespace Offramp.Refactoring.ChangeSets;

/// <summary>A file moved without any change to its bytes. Paths are repository-relative.</summary>
public sealed record FileRename(string From, string To, string Sha256);

/// <summary>An edit to an existing file (a project file, a solution): its exact bytes before and after.</summary>
public sealed record FileEdit(string Path, byte[] Before, byte[] After);

/// <summary>A new file.</summary>
public sealed record FileCreate(string Path, byte[] Content);

/// <summary>
/// What a writing command will do to the repository (docs/spec/00-architecture.md):
/// new files and edits, then renames. Rendered as a dry-run diff, applied through a
/// journal, and rolled back from it.
/// </summary>
public sealed class ChangeSet
{
    public List<FileCreate> Creates { get; } = [];

    public List<FileEdit> Edits { get; } = [];

    public List<FileRename> Renames { get; } = [];

    public bool IsEmpty => Creates.Count == 0 && Edits.Count == 0 && Renames.Count == 0;

    /// <summary>Records a rename, hashing the file as it is now so the move can prove it changed nothing.</summary>
    public void Rename(string repositoryRoot, string from, string to) =>
        Renames.Add(new FileRename(from, to, ContentHash.Sha256File(Path.Combine(repositoryRoot, from))));

    /// <summary>Records an edit; an edit that changes nothing is dropped, and a second edit to the same file replaces the first's result.</summary>
    public void Edit(string path, byte[] before, byte[] after)
    {
        var existing = Edits.FindIndex(e => e.Path == path);
        if (existing >= 0)
        {
            Edits[existing] = Edits[existing] with { After = after };
            return;
        }

        if (!before.AsSpan().SequenceEqual(after))
        {
            Edits.Add(new FileEdit(path, before, after));
        }
    }

    public void Create(string path, string content) => Creates.Add(new FileCreate(path, new UTF8Encoding(false).GetBytes(content)));

    /// <summary>A unified diff of the new and edited files, then the renames, in path order.</summary>
    public string Preview()
    {
        var builder = new StringBuilder();
        foreach (var create in Creates.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            builder.Append(UnifiedDiff.Render(create.Path, null, Decode(create.Content)));
        }

        foreach (var edit in Edits.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            builder.Append(UnifiedDiff.Render(edit.Path, Decode(edit.Before), Decode(edit.After)));
        }

        foreach (var rename in Renames.OrderBy(r => r.From, StringComparer.Ordinal))
        {
            builder.Append("rename from ").Append(rename.From).Append('\n').Append("rename to ").Append(rename.To).Append('\n');
        }

        return builder.ToString();
    }

    private static string Decode(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
