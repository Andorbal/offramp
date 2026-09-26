using Offramp.Refactoring.Moves;

namespace Offramp.Refactoring.Forwarders;

/// <summary>A public type that moved from the source to the destination and is forwarded.</summary>
public sealed record ForwardedType
{
    /// <summary>The type's metadata name (<c>Ns.Name</c>, with <c>`N</c> for generic arity).</summary>
    public required string Type { get; init; }

    /// <summary>The file declaring it now, in the destination, repository-relative.</summary>
    public required string File { get; init; }
}

/// <summary>A string naming a moved type together with the source assembly, which forwarders do not fix.</summary>
public sealed record StringReference
{
    public required string File { get; init; }

    public required int Line { get; init; }

    /// <summary>The moved type named (metadata name).</summary>
    public required string Type { get; init; }

    /// <summary>The line, trimmed.</summary>
    public required string Text { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp forwarders</c> (<c>schemas/v1/forwarders.json</c>).</summary>
public sealed record ForwardersResult
{
    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>The revision the source's former public types were read at (<c>--since</c>); null for the last scan's compilation.</summary>
    public string? Since { get; init; }

    public required IReadOnlyList<ForwardedType> Forwarded { get; init; }

    /// <summary>The generated file, repository-relative; null when nothing is forwarded.</summary>
    public string? File { get; init; }

    public required IReadOnlyList<ProjectEdit> ProjectEdits { get; init; }

    public required IReadOnlyList<StringReference> StringReferences { get; init; }

    public bool Applied { get; init; }

    /// <summary>The journal of an applied run; <c>move rollback --journal</c> undoes it.</summary>
    public string? Journal { get; init; }

    /// <summary>The unified diff of the new or changed files; null once applied.</summary>
    public string? Preview { get; init; }
}
