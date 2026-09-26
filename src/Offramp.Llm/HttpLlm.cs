using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Offramp.Llm;

/// <summary>What the adapters share: one JSON POST with a timeout, retries with backoff, and usage logging.</summary>
public abstract class HttpLlm(LlmSettings settings, HttpClient http, Action<LlmUsage>? log) : ILlm
{
    protected LlmSettings Settings { get; } = settings;

    protected HttpClient Http { get; } = http;

    public abstract Task<string> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);

    public abstract Task<JsonNode> CompleteJsonAsync(LlmRequest request, CancellationToken cancellationToken);

    protected string Endpoint(string path) => Settings.Url.TrimEnd('/') + "/" + path;

    /// <summary>Sends a request, retrying timeouts, connection failures, 429, and 5xx; returns the parsed JSON body.</summary>
    protected async Task<JsonNode> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken)
    {
        var delay = Settings.Backoff;
        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Settings.Timeout);
            string? problem;
            try
            {
                using var message = create();
                using var response = await Http.SendAsync(message, timeout.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return JsonNode.Parse(body) ?? throw new LlmException("The model's endpoint answered with an empty body.");
                }

                problem = $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}";
                if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    throw new LlmException($"The model's endpoint refused the request ({problem}).");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                problem = $"no answer within {Settings.Timeout.TotalSeconds:0} s";
            }
            catch (HttpRequestException e)
            {
                problem = e.Message;
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new LlmException("The model's endpoint answered with something that is not JSON.", e);
            }

            if (attempt >= Settings.Retries)
            {
                throw new LlmException($"The model's endpoint {Settings.Url} failed after {attempt + 1} attempt{(attempt == 0 ? "" : "s")}: {problem}");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay += delay;
        }
    }

    protected static HttpContent Json(JsonNode body) => new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

    protected void Log(string use, string model, int? input, int? output, Stopwatch watch) => log?.Invoke(new LlmUsage(use, model, input, output, watch.Elapsed));

    protected static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
