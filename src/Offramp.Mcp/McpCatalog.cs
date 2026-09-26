using System.Text.Json;
using System.Text.Json.Nodes;

namespace Offramp.Mcp;

/// <summary>One tool: its name, description, input schema, and how to run it.</summary>
public sealed record McpToolDefinition(string Name, string Description, JsonObject InputSchema, Func<McpToolCall, CancellationToken, Task<McpToolOutput>> InvokeAsync);

/// <summary>A tool call: the arguments as the client sent them, and where progress goes.</summary>
public sealed record McpToolCall(IReadOnlyDictionary<string, JsonElement> Arguments, IProgress<McpProgress> Progress);

/// <summary>A progress notification: a value that only grows, an optional total, and a message.</summary>
public readonly record struct McpProgress(double Progress, double? Total, string? Message);

/// <summary>A tool's answer: text blocks (the command's JSON envelope first), and whether the call failed.</summary>
public sealed record McpToolOutput(IReadOnlyList<string> Texts, bool IsError);

/// <summary>A concrete resource, listed by <c>resources/list</c>.</summary>
public sealed record McpResourceDefinition(string Uri, string Name, string Description, string MimeType);

/// <summary>A family of resources, listed by <c>resources/templates/list</c>.</summary>
public sealed record McpResourceTemplateDefinition(string UriTemplate, string Name, string Description, string MimeType);

/// <summary>What the server exposes. The lists are read at each request, so new plans appear without a restart.</summary>
public sealed record McpCatalog
{
    public required string ServerName { get; init; }

    public required string ServerVersion { get; init; }

    /// <summary>Guidance for the client's model, sent at initialization.</summary>
    public string? Instructions { get; init; }

    public required IReadOnlyList<McpToolDefinition> Tools { get; init; }

    public Func<IReadOnlyList<McpResourceDefinition>> Resources { get; init; } = () => [];

    public IReadOnlyList<McpResourceTemplateDefinition> ResourceTemplates { get; init; } = [];

    /// <summary>A resource's text and MIME type, or null when there is no such resource.</summary>
    public Func<string, CancellationToken, Task<(string Text, string MimeType)?>> ReadResourceAsync { get; init; } = (_, _) => Task.FromResult<(string, string)?>(null);
}
