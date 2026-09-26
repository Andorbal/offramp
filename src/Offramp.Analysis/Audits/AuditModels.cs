using System.Text.Json.Serialization;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;

namespace Offramp.Analysis.Audits;

[JsonConverter(typeof(CamelCaseEnumConverter<AuditKind>))]
public enum AuditKind
{
    Api,
    Behavior,
    Serialization,
    Native,
}

/// <summary>A rule from a pack (<c>rules/audit-*.yml</c>): what it matches and what to do about it.</summary>
public sealed record AuditRule
{
    public required string Id { get; init; }

    public required AuditKind Audit { get; init; }

    /// <summary><c>core</c>, <c>web</c>, <c>desktop</c>, <c>data</c>, <c>serialization</c>, or <c>native</c>.</summary>
    public required string Pack { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>A lower severity below a target major version (<c>OFR3201</c>: an error only from .NET 9).</summary>
    public (int Target, Severity Severity)? SeverityBelowTarget { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string Recommendation { get; init; }

    /// <summary>Documentation IDs; a <c>T:</c> type covers its members, an <c>N:</c> namespace everything inside, an <c>M:</c> without parameters every overload.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>Types whose subclasses match (declarations deriving from them).</summary>
    public IReadOnlyList<string> BaseTypes { get; init; } = [];

    /// <summary>Attribute types (or their base types) whose use matches.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>A named matcher over the semantic model, or null.</summary>
    public string? Matcher { get; init; }

    public string HelpUri => "https://offramp.dev/diagnostics/" + Id;

    /// <summary>The severity for a target major version.</summary>
    public Severity SeverityFor(int target) =>
        SeverityBelowTarget is { } below && target < below.Target ? below.Severity : Severity;
}

/// <summary>One place a rule matched.</summary>
public sealed record AuditFinding
{
    public required string Rule { get; init; }

    public required Severity Severity { get; init; }

    /// <summary>True when offramp.yml changed the rule's severity.</summary>
    public bool Overridden { get; init; }

    public required string Project { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    /// <summary>The symbol involved (fully qualified), or the construct for pattern rules.</summary>
    public required string Symbol { get; init; }

    /// <summary>The namespace of the symbol, when it has one (the porting ledger counts by it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }

    public required string Recommendation { get; init; }

    /// <summary>Rule-specific facts (a P/Invoke's library, a serialization flow's evidence, an assembly mapping).</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Findings per rule, for the summary table.</summary>
public sealed record AuditRuleCount(string Rule, string Title, Severity Severity, int Findings, int Projects);

/// <summary>`audit api`'s porting ledger for one project.</summary>
public sealed record ProjectPortability
{
    public required string Project { get; init; }

    public required int Files { get; init; }

    /// <summary>Files with no error-level finding.</summary>
    public required int PortableFiles { get; init; }

    /// <summary><see cref="PortableFiles"/> over <see cref="Files"/>, rounded to three places.</summary>
    public required double Portability { get; init; }

    public required IReadOnlyDictionary<string, int> ByCategory { get; init; }

    public required IReadOnlyDictionary<string, int> ByNamespace { get; init; }
}

/// <summary>A namespace and how many findings name it, across the solution.</summary>
public sealed record NamespaceCount(string Namespace, int Findings);

/// <summary>The <c>result</c> of <c>offramp audit &lt;kind&gt;</c> (<c>schemas/v1/audit.json</c>).</summary>
public sealed record AuditResult
{
    public required AuditKind Audit { get; init; }

    public required string Target { get; init; }

    /// <summary>The projects audited (C# projects with a compiler log).</summary>
    public required IReadOnlyList<string> Projects { get; init; }

    /// <summary>The rules that ran (disabled packs aside).</summary>
    public required IReadOnlyList<string> Rules { get; init; }

    public required IReadOnlyList<AuditRuleCount> Summary { get; init; }

    public required IReadOnlyList<AuditFinding> Findings { get; init; }

    /// <summary><c>audit api</c> only: portability per project.</summary>
    public IReadOnlyList<ProjectPortability> Ledger { get; init; } = [];

    /// <summary><c>audit api</c> only: the 20 namespaces with the most findings (what `seams` consumes).</summary>
    public IReadOnlyList<NamespaceCount> TopNamespaces { get; init; } = [];

    /// <summary>Projects that could not be audited (or not compiled against the target), with why.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>The file <c>--out</c> received (SARIF, Markdown, or JSON), repository-relative; null otherwise.</summary>
    public string? Output { get; init; }
}
