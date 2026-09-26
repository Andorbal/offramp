namespace Offramp.Analysis.Seams;

/// <summary>A type that cannot port: it uses unportable symbols itself, or inherits or exposes a type that does.</summary>
public sealed record TaintedType(string Type, IReadOnlyList<string> Reason, int Loc);

/// <summary>Tainted types that reference each other in a cycle: they move together.</summary>
public sealed record SeamPartition(int Id, IReadOnlyList<string> Types, int Loc);

/// <summary>A member of the boundary type that code on the clean side calls.</summary>
public sealed record SeamMember
{
    /// <summary>The member as C# declares it: <c>Accounts.Directory.DirectoryEntryInfo FindUser(string samAccountName)</c>.</summary>
    public required string Signature { get; init; }

    public required int CallSites { get; init; }

    /// <summary>Every parameter and the return type can cross a network boundary as data.</summary>
    public required bool WireFriendly { get; init; }

    /// <summary>Static members need an instance wrapper before they can be on an interface (OFR4003).</summary>
    public bool Static { get; init; }

    /// <summary>Why the member is not wire-friendly, one entry per offending parameter or return type.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>Where an interface can go: clean callers on one side, a tainted boundary type on the other.</summary>
public sealed record Seam
{
    public required string Id { get; init; }

    public required string BoundaryType { get; init; }

    public required IReadOnlyList<string> Callers { get; init; }

    public required IReadOnlyList<SeamMember> Members { get; init; }

    public required string ProposedInterface { get; init; }

    /// <summary>0–1: fewer members, more wire-friendly members, and an articulation point score higher.</summary>
    public required double Score { get; init; }

    /// <summary>Extracting the boundary type disconnects every clean type from the taint.</summary>
    public required bool ArticulationPoint { get; init; }

    /// <summary><c>llm</c> when the model named the interface (<c>--llm</c>, <c>llm.uses: naming</c>); absent otherwise.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }
}

public sealed record SeamExtraction(string MoveToProject, IReadOnlyList<string> Types, int EstimatedLoc);

/// <summary>A type-to-type reference in the project, for the graph views.</summary>
public sealed record SeamEdge(string From, string To, int Weight, bool Cut);

/// <summary>The <c>result</c> of <c>offramp seams</c> (<c>schemas/v1/seams.json</c>).</summary>
public sealed record SeamsResult
{
    public required string Project { get; init; }

    /// <summary><c>audit</c> or <c>list</c>.</summary>
    public required string UnportableFrom { get; init; }

    public required IReadOnlyList<TaintedType> Tainted { get; init; }

    public required IReadOnlyList<SeamPartition> Partitions { get; init; }

    /// <summary>Ranked: fewest members, most callers, most wire-friendly first.</summary>
    public required IReadOnlyList<Seam> Seams { get; init; }

    public SeamExtraction? Extraction { get; init; }

    /// <summary>Every type of the project (for the graph views).</summary>
    public required IReadOnlyList<string> Types { get; init; }

    public required IReadOnlyList<SeamEdge> Edges { get; init; }
}
