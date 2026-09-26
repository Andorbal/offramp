using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Remote;

public sealed record RemoteRequest
{
    public required string RepositoryRoot { get; init; }

    /// <summary>The project that declares the interface and its implementation.</summary>
    public required ProjectInfo Project { get; init; }

    /// <summary>The interface, fully qualified.</summary>
    public required string Interface { get; init; }

    /// <summary>The implementation, fully qualified; default the one class in the project that implements the interface.</summary>
    public string? Implementation { get; init; }

    /// <summary><c>auto</c>, <c>net10-windows</c>, or <c>net48</c>.</summary>
    public string HostFramework { get; init; } = "auto";

    /// <summary><c>stj</c> or <c>newtonsoft</c>: the client's JSON serializer.</summary>
    public string Serializer { get; init; } = "stj";

    public string? HostDirectory { get; init; }

    public string? ClientDirectory { get; init; }

    public string? ContractsDirectory { get; init; }

    public bool Container { get; init; }

    public bool AsyncVariant { get; init; }

    /// <summary>Interface members left out of the boundary: the client throws <c>NotSupportedException</c> for them.</summary>
    public IReadOnlyList<string> SkipMembers { get; init; } = [];

    /// <summary>The solution the next steps add the projects to, repository-relative.</summary>
    public string? Solution { get; init; }

    /// <summary>Resolves net10.0-windows references for the <c>auto</c> trial compilation; null chooses net48.</summary>
    public TargetReferenceResolver? References { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>An interface member and what happens to it at the boundary.</summary>
public sealed record RemoteMember
{
    public required string Name { get; init; }

    /// <summary>The member as C# declares it.</summary>
    public required string Signature { get; init; }

    /// <summary><c>/IDirectoryLookup/FindUser</c>; null for members that do not cross.</summary>
    public string? Route { get; init; }

    /// <summary><c>remote</c>, <c>skipped</c> (--skip-member), or <c>blocked</c> (OFR4002).</summary>
    public required string Status { get; init; }

    /// <summary>The member is synchronous, so the client blocks on the HTTP call (OFR4020).</summary>
    public bool Sync { get; init; }

    /// <summary>Why the member cannot cross the wire.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>A type from the member signatures and the data-only copy the contracts project declares.</summary>
public sealed record RemoteDto(string Type, string Dto);

/// <summary>A generated project: <c>contracts</c>, <c>host</c>, or <c>client</c>.</summary>
public sealed record GeneratedProject(string Role, string Path, IReadOnlyList<string> TargetFrameworks);

/// <summary>The <c>result</c> of <c>offramp remote</c> (<c>schemas/v1/remote.json</c>).</summary>
public sealed record RemoteResult
{
    public required string Project { get; init; }

    public required string Interface { get; init; }

    public string? Implementation { get; init; }

    /// <summary><c>net10.0-windows</c> or <c>net48</c>; null when nothing was generated.</summary>
    public string? HostFramework { get; init; }

    /// <summary>Why that host framework: the choice, or what the trial compilation found.</summary>
    public string? HostFrameworkReason { get; init; }

    public string Transport { get; init; } = "http-json";

    public required string Serializer { get; init; }

    public required IReadOnlyList<RemoteMember> Members { get; init; }

    public IReadOnlyList<RemoteDto> Dtos { get; init; } = [];

    public IReadOnlyList<GeneratedProject> Projects { get; init; } = [];

    /// <summary>Source files the net10.0-windows host compiles from the implementation's project (links, not copies).</summary>
    public IReadOnlyList<string> LinkedSources { get; init; } = [];

    /// <summary>Every file written, repository-relative.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The DI switch to call where the application registers its services.</summary>
    public string? Registration { get; init; }

    public IReadOnlyList<string> NextSteps { get; init; } = [];

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record RemotePlan(RemoteResult Result, ChangeSet? ChangeSet);
