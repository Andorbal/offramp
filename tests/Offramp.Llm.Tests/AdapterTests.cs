using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Offramp.Llm.Tests;

/// <summary>The adapters against a recorded HTTP exchange: what they send, and what they make of the answer.</summary>
public sealed class AdapterTests
{
    private static readonly JsonObject NameSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("name"),
        ["additionalProperties"] = false,
    };

    [Fact]
    public async Task Openai_compatible_discovers_the_model_and_asks_for_schema_constrained_json()
    {
        var handler = new RecordingHandler(
            Ok("""{"data":[{"id":"local-model"},{"id":"other"}]}"""),
            Ok("""{"choices":[{"message":{"content":"```json\n{\"name\":\"IDirectoryLookup\"}\n```"}}],"usage":{"prompt_tokens":12,"completion_tokens":5}}"""));
        var usages = new List<LlmUsage>();
        var llm = LlmFactory.Create(new LlmSettings { Url = "http://llm.test/v1/", ApiKey = "secret" }, new HttpClient(handler), usages.Add);

        var answer = await llm.CompleteJsonAsync(new LlmRequest { Use = "naming", System = "Name things.", Prompt = "Name it.", Schema = NameSchema }, TestContext.Current.CancellationToken);

        Assert.Equal("IDirectoryLookup", answer["name"]!.GetValue<string>());
        Assert.Equal(["GET http://llm.test/v1/models", "POST http://llm.test/v1/chat/completions"], handler.Requests.Select(r => r.Method + " " + r.Uri));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer secret", r.Authorization));
        var body = JsonNode.Parse(handler.Requests[1].Body)!;
        Assert.Equal("local-model", body["model"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal(0, body["temperature"]!.GetValue<int>());
        var usage = Assert.Single(usages);
        Assert.Equal(("naming", "local-model", 12, 5), (usage.Use, usage.Model, usage.InputTokens!.Value, usage.OutputTokens!.Value));
    }

    [Fact]
    public async Task Anthropic_forces_the_answer_tool_and_reads_its_input()
    {
        var handler = new RecordingHandler(Ok("""
            {"content":[{"type":"text","text":"Here it is."},{"type":"tool_use","name":"answer","input":{"name":"IMailer"}}],"usage":{"input_tokens":20,"output_tokens":8}}
            """));
        var llm = LlmFactory.Create(new LlmSettings { Provider = "anthropic", Url = LlmFactory.AnthropicUrl, Model = "claude-test", ApiKey = "key" }, new HttpClient(handler));

        var answer = await llm.CompleteJsonAsync(new LlmRequest { Use = "naming", Prompt = "Name it.", Schema = NameSchema }, TestContext.Current.CancellationToken);

        Assert.Equal("IMailer", answer["name"]!.GetValue<string>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST https://api.anthropic.com/v1/messages", request.Method + " " + request.Uri);
        Assert.Equal(AnthropicLlm.ApiVersion, request.Headers["anthropic-version"]);
        Assert.Equal("key", request.Headers["x-api-key"]);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("answer", body["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("object", body["tools"]![0]!["input_schema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_errors_are_retried_and_client_errors_are_not()
    {
        var retried = new RecordingHandler(Status(HttpStatusCode.ServiceUnavailable), Status(HttpStatusCode.TooManyRequests), Ok("""{"choices":[{"message":{"content":"ok"}}]}"""));
        var refused = new RecordingHandler(Status(HttpStatusCode.Unauthorized), Ok("{}"));
        var exhausted = new RecordingHandler(Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway), Status(HttpStatusCode.BadGateway));
        LlmSettings Settings() => new() { Url = "http://llm.test/v1", Model = "m", Backoff = TimeSpan.FromMilliseconds(1) };
        var request = new LlmRequest { Use = "summarizing", Prompt = "Summarize." };

        var answer = await LlmFactory.Create(Settings(), new HttpClient(retried)).CompleteAsync(request, TestContext.Current.CancellationToken);
        var unauthorized = await Assert.ThrowsAsync<LlmException>(() => LlmFactory.Create(Settings(), new HttpClient(refused)).CompleteAsync(request, TestContext.Current.CancellationToken));
        var failed = await Assert.ThrowsAsync<LlmException>(() => LlmFactory.Create(Settings(), new HttpClient(exhausted)).CompleteAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("ok", answer);
        Assert.Equal(3, retried.Requests.Count);
        Assert.Single(refused.Requests);
        Assert.Contains("401", unauthorized.Message, StringComparison.Ordinal);
        Assert.Contains("after 3 attempts", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_needs_a_model_and_an_unknown_provider_is_refused()
    {
        var llm = LlmFactory.Create(new LlmSettings { Provider = "anthropic", Url = LlmFactory.AnthropicUrl }, new HttpClient(new RecordingHandler()));

        await Assert.ThrowsAsync<LlmException>(() => llm.CompleteAsync(new LlmRequest { Use = "naming", Prompt = "x" }, TestContext.Current.CancellationToken));
        Assert.Throws<LlmException>(() => LlmFactory.Create(new LlmSettings { Provider = "other", Url = "http://x" }));
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{\"error\":\"nope\"}") };

    private sealed record Recorded(string Method, string Uri, string Body, string? Authorization, Dictionary<string, string> Headers);

    /// <summary>Answers with the given responses in order and records each request.</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
