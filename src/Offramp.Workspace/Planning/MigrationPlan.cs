using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Planning;

/// <summary>One project in the migration order.</summary>
public sealed record PlanEntry
{
    public required string Project { get; init; }

    public required string Name { get; init; }

    public required ProjectKind Kind { get; init; }

    public required FrameworkClass FrameworkClass { get; init; }

    /// <summary>
    /// 0 for projects already portable; 1 for projects portable today; n for projects
    /// whose framework-only dependencies are all in earlier waves. Members of a cycle share a wave.
    /// </summary>
    public required int Wave { get; init; }

    /// <summary>Projects that depend on this one, directly or transitively, in the whole model.</summary>
    public required int BlastRadius { get; init; }

    /// <summary>Framework-only projects this one depends on, directly or transitively.</summary>
    public required IReadOnlyList<string> Blockers { get; init; }

    public required ProjectReadiness Readiness { get; init; }

    public required bool InCycle { get; init; }
}

public sealed record PlanCounts(int Projects, int Done, int Ready, int Blocked, int Waves);

/// <summary>The <c>result</c> of <c>offramp plan</c> (<c>schemas/v1/plan.json</c>).</summary>
public sealed record PlanResult
{
    /// <summary>The project given to <c>--for</c>, resolved to its path, or null.</summary>
    public string? For { get; init; }

    public required bool Frontier { get; init; }

    public required IReadOnlyList<ProjectKind> ExcludeKinds { get; init; }

    /// <summary>Wave, then blast radius (largest first), then path: every project after the framework-only projects it depends on.</summary>
    public required IReadOnlyList<PlanEntry> Order { get; init; }

    /// <summary>Reference cycles among the listed projects; their members cannot be ported one at a time.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; }

    public required PlanCounts Counts { get; init; }
}
