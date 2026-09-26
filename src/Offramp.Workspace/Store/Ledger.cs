using System.Text.Json.Serialization;
using Offramp.Core.Caching;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Store;

public sealed record LedgerProject(string Id, ProjectKind Kind, FrameworkClass FrameworkClass, int Loc, int Packages);

public sealed record ClassTotals(int Projects, int Loc);

public sealed record LedgerTotals
{
    public int Projects { get; init; }

    public int Loc { get; init; }

    public SortedDictionary<string, ClassTotals> ByFrameworkClass { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, int> ByKind { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One point on the migration burn-down: small, committed to the repository,
/// read by <c>report</c> (docs/spec/02-workspace-model.md#ledger-and-snapshots).
/// </summary>
public sealed record LedgerSnapshot
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/ledger.json";

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaUri;

    [JsonPropertyOrder(-1)]
    public int Version { get; init; } = 1;

    public required string CreatedAt { get; init; }

    public string? Solution { get; init; }

    public required LedgerTotals Totals { get; init; }

    public required IReadOnlyList<LedgerProject> Projects { get; init; }
}

public static class Ledger
{
    public static LedgerSnapshot Snapshot(WorkspaceModel model)
    {
        var projects = model.Projects
            .Select(p => new LedgerProject(p.Id, p.Kind, p.FrameworkClass, p.Loc, p.PackageReferences.Count))
            .ToList();
        var byClass = new SortedDictionary<string, ClassTotals>(StringComparer.Ordinal);
        foreach (var frameworkClass in Enum.GetValues<FrameworkClass>())
        {
            var members = projects.Where(p => p.FrameworkClass == frameworkClass).ToList();
            byClass[Wire(frameworkClass)] = new ClassTotals(members.Count, members.Sum(p => p.Loc));
        }

        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<ProjectKind>())
        {
            byKind[Wire(kind)] = projects.Count(p => p.Kind == kind);
        }

        return new LedgerSnapshot
        {
            CreatedAt = model.CreatedAt,
            Solution = model.Solution,
            Totals = new LedgerTotals { Projects = projects.Count, Loc = projects.Sum(p => p.Loc), ByFrameworkClass = byClass, ByKind = byKind },
            Projects = projects,
        };
    }

    /// <summary>
    /// Writes <c>&lt;ledger&gt;/&lt;yyyy-MM-dd&gt;-&lt;hash&gt;.json</c>; the hash covers everything
    /// but the timestamp, so rescanning an unchanged repository on the same day
    /// rewrites the same file. Returns the repository-relative path.
    /// </summary>
    public static string Write(LedgerSnapshot snapshot, string ledgerDirectory, string repositoryRoot)
    {
        var typeInfo = WorkspaceJsonContext.Default.LedgerSnapshot;
        var identity = OfframpJson.Serialize(snapshot with { CreatedAt = "" }, typeInfo);
        var hash = ContentHash.Sha256(identity)[..8];
        var date = snapshot.CreatedAt.Length >= 10 ? snapshot.CreatedAt[..10] : "undated";
        Directory.CreateDirectory(ledgerDirectory);
        var path = Path.Combine(ledgerDirectory, $"{date}-{hash}.json");
        File.WriteAllText(path, OfframpJson.Serialize(snapshot, typeInfo), new System.Text.UTF8Encoding(false));
        return RepoPaths.ToRepositoryRelative(repositoryRoot, path);
    }

    private static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
