using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Offramp.Llm;

/// <summary>
/// The Anthropic Messages API (<c>/v1/messages</c>, <c>anthropic-version</c> 2023-06-01). JSON
/// answers are a forced call of a single tool whose input schema is the requested schema.
/// A model is required.
/// </summary>
public sealed class AnthropicLlm(LlmSettings settings, HttpClient http, Action<LlmUsage>? log = null) : HttpLlm(settings, http, log)
{
    public const string ApiVersion = "2023-06-01";

    private const string AnswerTool = "answer";

    public override async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var response = await MessagesAsync(request, tools: null, cancellationToken).ConfigureAwait(false);
        var text = string.Concat(response["content"]?.AsArray()
            .Where(c => c?["type"]?.GetValue<string>() == "text")
            .Select(c => c!["text"]?.GetValue<string>()) ?? []);
        return text.Length == 0 ? throw new LlmException("The model's answer has no text.") : text.Trim();
    }

    public override async Task<JsonNode> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var schema = request.Schema ?? throw new ArgumentException("A JSON answer needs a schema.", nameof(request));
        var tools = new JsonArray { new JsonObject { ["name"] = AnswerTool, ["description"] = "Give the answer.", ["input_schema"] = schema.DeepClone() } };
        var response = await MessagesAsync(request, tools, cancellationToken).ConfigureAwait(false);
        var use = response["content"]?.AsArray().FirstOrDefault(c => c?["type"]?.GetValue<string>() == "tool_use" && c["name"]?.GetValue<string>() == AnswerTool);
        return use?["input"]?.DeepClone() ?? throw new LlmException("The model did not give its answer through the answer tool.");
    }

    private async Task<JsonNode> MessagesAsync(LlmRequest request, JsonArray? tools, CancellationToken cancellationToken)
    {
        var model = Settings.Model ?? throw new LlmException("The Anthropic provider needs llm.model.");
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = 0,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = request.Prompt } },
        };
        if (request.System is { } system)
        {
            body["system"] = system;
        }

        if (tools is not null)
        {
            body["tools"] = tools;
            body["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = AnswerTool };
        }

        var watch = Stopwatch.StartNew();
        var response = await SendAsync(() =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("messages")) { Content = Json(body) };
            message.Headers.Add("anthropic-version", ApiVersion);
            if (Settings.ApiKey is { Length: > 0 } key)
            {
                message.Headers.Add("x-api-key", key);
            }

            return message;
        }, cancellationToken).ConfigureAwait(false);
        Log(request.Use, model, Int(response["usage"]?["input_tokens"]), Int(response["usage"]?["output_tokens"]), watch);
        return response;
    }
}
