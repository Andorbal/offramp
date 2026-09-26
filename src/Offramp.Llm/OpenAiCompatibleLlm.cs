using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Offramp.Llm;

/// <summary>
/// Any <c>/v1/chat/completions</c> endpoint: OpenAI, LiteLLM, OpenRouter, LM Studio, Ollama,
/// vLLM. Without a model, the first one <c>/v1/models</c> lists. JSON answers use
/// <c>response_format: json_schema</c>; temperature is 0.
/// </summary>
public sealed class OpenAiCompatibleLlm(LlmSettings settings, HttpClient http, Action<LlmUsage>? log = null) : HttpLlm(settings, http, log)
{
    private string? _model = settings.Model;

    public override async Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var answer = await ChatAsync(request, responseFormat: null, cancellationToken).ConfigureAwait(false);
        return answer.Trim();
    }

    public override async Task<JsonNode> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var schema = request.Schema ?? throw new ArgumentException("A JSON answer needs a schema.", nameof(request));
        var format = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject { ["name"] = "answer", ["strict"] = true, ["schema"] = schema.DeepClone() },
        };
        var answer = await ChatAsync(request, format, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(Unfence(answer)) ?? throw new LlmException("The model answered with empty JSON.");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new LlmException("The model's answer is not the JSON asked for.", e);
        }
    }

    private async Task<string> ChatAsync(LlmRequest request, JsonObject? responseFormat, CancellationToken cancellationToken)
    {
        var model = await ModelAsync(cancellationToken).ConfigureAwait(false);
        var messages = new JsonArray();
        if (request.System is { } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = request.Prompt });
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["temperature"] = 0, ["max_tokens"] = request.MaxTokens };
        if (responseFormat is not null)
        {
            body["response_format"] = responseFormat;
        }

        var watch = Stopwatch.StartNew();
        var response = await SendAsync(() => Post("chat/completions", body), cancellationToken).ConfigureAwait(false);
        Log(request.Use, model, Int(response["usage"]?["prompt_tokens"]), Int(response["usage"]?["completion_tokens"]), watch);
        return response["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? throw new LlmException("The model's answer has no message content.");
    }

    /// <summary>The configured model, else the first one the server lists.</summary>
    private async Task<string> ModelAsync(CancellationToken cancellationToken)
    {
        if (_model is not null)
        {
            return _model;
        }

        var models = await SendAsync(() => Authorize(new HttpRequestMessage(HttpMethod.Get, Endpoint("models"))), cancellationToken).ConfigureAwait(false);
        _model = models["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).FirstOrDefault(id => id is not null)
            ?? throw new LlmException($"{Settings.Url} lists no models; set llm.model.");
        return _model;
    }

    private HttpRequestMessage Post(string path, JsonNode body) => Authorize(new HttpRequestMessage(HttpMethod.Post, Endpoint(path)) { Content = Json(body) });

    private HttpRequestMessage Authorize(HttpRequestMessage message)
    {
        if (Settings.ApiKey is { Length: > 0 } key)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return message;
    }

    /// <summary>Some local servers wrap JSON in a Markdown fence despite the response format.</summary>
    private static string Unfence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start < 0 || end <= start ? trimmed : trimmed[(start + 1)..end].Trim();
    }
}
