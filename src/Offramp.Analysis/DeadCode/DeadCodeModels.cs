using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Analysis.DeadCode;

[JsonConverter(typeof(CamelCaseEnumConverter<DeadCodeConfidence>))]
public enum DeadCodeConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>A type or member nothing in the solution references.</summary>
public sealed record DeadCodeCandidate
{
    /// <summary>Fully qualified name.</summary>
    public required string Symbol { get; init; }

    /// <summary><c>class</c>, <c>struct</c>, <c>interface</c>, <c>enum</c>, <c>delegate</c>, <c>method</c>, <c>property</c>, <c>field</c>, or <c>event</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The effective accessibility: <c>public</c> when visible outside the assembly, else <c>internal</c> or <c>private</c>.</summary>
    public required string Accessibility { get; init; }

    public required DeadCodeConfidence Confidence { get; init; }

    /// <summary>Why the confidence is what it is, one fact per entry.</summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    /// <summary>Lines the declaration spans (with its attributes and documentation comment).</summary>
    public required int Loc { get; init; }

    /// <summary><c>llm</c> when the model's classification was added to the evidence (<c>llm.uses: classifying</c>); absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }
}

/// <summary>A production symbol only test projects reference (OFR3402, with <c>--include-tests</c>).</summary>
public sealed record TestOnlySymbol
{
    public required string Symbol { get; init; }

    public required string Kind { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public required int Loc { get; init; }

    /// <summary>The test projects that reference it.</summary>
    public required IReadOnlyList<string> Tests { get; init; }
}

public sealed record DeadCodeLoc(int High, int Medium, int Low);

public sealed record DeadCodeProject
{
    public required string Project { get; init; }

    public required IReadOnlyList<DeadCodeCandidate> Candidates { get; init; }

    public IReadOnlyList<TestOnlySymbol> TestOnly { get; init; } = [];

    /// <summary>Lines the candidates span, by confidence.</summary>
    public required DeadCodeLoc Loc { get; init; }
}

public sealed record DeadCodeSummary
{
    public required int Candidates { get; init; }

    public required int High { get; init; }

    public required int Medium { get; init; }

    public required int Low { get; init; }

    public required int TestOnly { get; init; }

    /// <summary>Lines removable at high confidence: the stakeholder number.</summary>
    public required int RemovableLoc { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp audit dead-code</c> (<c>schemas/v1/dead-code.json</c>).</summary>
public sealed record DeadCodeResult
{
    /// <summary><c>public</c> (only symbols visible outside their assembly) or <c>all</c>.</summary>
    public required string Scope { get; init; }

    public required DeadCodeConfidence MinConfidence { get; init; }

    public required bool IncludeTests { get; init; }

    /// <summary>Projects with candidates, by id.</summary>
    public required IReadOnlyList<DeadCodeProject> Projects { get; init; }

    public required DeadCodeSummary Summary { get; init; }

    /// <summary>Projects not analyzed, with why.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}
