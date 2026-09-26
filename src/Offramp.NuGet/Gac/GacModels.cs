using Offramp.Analysis.Rules;

namespace Offramp.NuGet.Gac;

public sealed record GacReference
{
    public required string Name { get; init; }

    public required FrameworkAssemblyMapping Mapping { get; init; }

    /// <summary>Names in source that bind to this assembly, or null when no compilation is available (or the project is not C#).</summary>
    public int? Usages { get; init; }
}

public sealed record GacProject
{
    public required string Project { get; init; }

    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    public required IReadOnlyList<GacReference> References { get; init; }
}

public sealed record GacSummary(int Builtin, int Package, int CompatPack, int None, int Unknown, int Unused);

/// <summary>The result of <c>offramp deps gac</c> (<c>schemas/v1/deps-gac.json</c>).</summary>
public sealed record GacResult
{
    public required string Target { get; init; }

    public required IReadOnlyList<GacProject> Projects { get; init; }

    public required GacSummary Summary { get; init; }
}
