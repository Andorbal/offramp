using System.Text.Json.Nodes;

namespace Offramp.Llm;

/// <summary>One question to the model: a system instruction, the prompt, and for JSON answers the schema.</summary>
public sealed record LlmRequest
{
    /// <summary>The permitted use asking (<c>naming</c>, <c>ranking</c>, <c>summarizing</c>, <c>classifying</c>), for the log.</summary>
    public required string Use { get; init; }

    public string? System { get; init; }

    public required string Prompt { get; init; }

    /// <summary>The JSON schema of the answer, for <see cref="ILlm.CompleteJsonAsync"/>.</summary>
    public JsonObject? Schema { get; init; }

    public int MaxTokens { get; init; } = 512;
}

/// <summary>
/// A language model. Offramp asks it only for garnish: names, a ranking among candidates the
/// rules produced, a summary paragraph, a classification. Never for what to move, which
/// version to take, or whether something compiles.
/// </summary>
public interface ILlm
{
    /// <summary>Free text.</summary>
    Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);

    /// <summary>An answer constrained to <see cref="LlmRequest.Schema"/> (required).</summary>
    Task<JsonNode> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>Where the model is and how to reach it.</summary>
public sealed record LlmSettings
{
    /// <summary><c>openai</c> (any OpenAI-compatible endpoint) or <c>anthropic</c>.</summary>
    public string Provider { get; init; } = "openai";

    /// <summary>The API base URL, up to <c>/v1</c>.</summary>
    public required string Url { get; init; }

    /// <summary>Null: the first model an OpenAI-compatible server lists; required for Anthropic.</summary>
    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public int Retries { get; init; } = 2;

    /// <summary>Delay before the first retry; doubled for each next one.</summary>
    public TimeSpan Backoff { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>A failed call: the endpoint was unreachable, answered with an error, or the answer was unusable.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a call cost, reported at <c>--verbose</c>.</summary>
public sealed record LlmUsage(string Use, string Model, int? InputTokens, int? OutputTokens, TimeSpan Duration);

/// <summary>Creates the adapter a provider names.</summary>
public static class LlmFactory
{
    public const string AnthropicUrl = "https://api.anthropic.com/v1";

    /// <param name="settings">The provider settings.</param>
    /// <param name="http">The client to send requests with; null creates one.</param>
    /// <param name="log">Receives one entry per call.</param>
    public static ILlm Create(LlmSettings settings, HttpClient? http = null, Action<LlmUsage>? log = null) => settings.Provider.ToLowerInvariant() switch
    {
        "openai" => new OpenAiCompatibleLlm(settings, http ?? new HttpClient(), log),
        "anthropic" => new AnthropicLlm(settings, http ?? new HttpClient(), log),
        _ => throw new LlmException($"Unknown LLM provider '{settings.Provider}'; use openai or anthropic."),
    };
}
