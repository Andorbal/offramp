using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Offramp.Cli.Tests;

/// <summary>
/// An OpenAI-compatible endpoint on a loopback port: <c>GET /v1/models</c> lists one model and
/// <c>POST /v1/chat/completions</c> answers with what <paramref name="answer"/> returns for the
/// request body. Every request is recorded.
/// </summary>
public sealed class StubLlmServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<JsonNode, string> _answer;
    private readonly Task _loop;
    private readonly List<(string Path, JsonNode? Body)> _requests = [];

    public StubLlmServer(Func<JsonNode, string> answer)
    {
        _answer = answer;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public string Url => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1");

    public IReadOnlyList<(string Path, JsonNode? Body)> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The chat requests' user prompts.</summary>
    public IReadOnlyList<string> Prompts => [.. Requests.Where(r => r.Body is not null).Select(r => r.Body!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>())];

    /// <summary>Environment variables pointing <c>offramp</c> at this server.</summary>
    public void Configure(Dictionary<string, string> environment)
    {
        environment["OFFRAMP_LLM_PROVIDER"] = "openai";
        environment["OFFRAMP_LLM_URL"] = Url;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var (requestLine, body) = await ReadAsync(stream);
            var path = requestLine.Split(' ') is [_, var p, ..] ? p : "/";
            JsonNode? json = body.Length > 0 ? JsonNode.Parse(body) : null;
            lock (_requests)
            {
                _requests.Add((path, json));
            }

            var response = path.EndsWith("/models", StringComparison.Ordinal)
                ? """{"data":[{"id":"stub-model"}]}"""
                : new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = _answer(json!) } }),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 3 },
                }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(response);
            var head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(head);
            await stream.WriteAsync(bytes);
        }
    }

    /// <summary>The request line and the body (by Content-Length).</summary>
    private static async Task<(string RequestLine, string Body)> ReadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                return ("", "");
            }

            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        var headers = Encoding.ASCII.GetString([.. buffer.Take(headerEnd)]).Split("\r\n");
        var length = headers.Select(h => h.Split(':', 2)).Where(h => h.Length == 2 && h[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => int.Parse(h[1].Trim(), CultureInfo.InvariantCulture)).FirstOrDefault();
        while (buffer.Count - headerEnd - 4 < length)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }

            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        return (headers[0], Encoding.UTF8.GetString([.. buffer.Skip(headerEnd + 4).Take(length)]));
    }

    private static int IndexOf(List<byte> buffer, byte[] pattern)
    {
        for (var i = 0; i <= buffer.Count - pattern.Length; i++)
        {
            if (!pattern.Where((b, j) => buffer[i + j] != b).Any())
            {
                return i;
            }
        }

        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}
