namespace Offramp.Reporting.Graph;

/// <summary>The <c>result</c> of <c>offramp graph</c> (<c>schemas/v1/graph.json</c>).</summary>
public sealed record GraphResult
{
    /// <summary>The rendering format, or null when only the data was requested (the envelope carries it).</summary>
    public GraphFormat? Format { get; init; }

    /// <summary>Repository-relative path of the written file, or null.</summary>
    public string? Output { get; init; }

    public required GraphDocument Graph { get; init; }

    /// <summary>The rendering when it was not written to a file (dot, mermaid, html), else null.</summary>
    public string? Content { get; init; }
}
