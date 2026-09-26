using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Ide.Lsp;

namespace Offramp.Ide.Tests;

/// <summary>docs/spec/commands/ide.md#offramp-ide-serve: the protocol, end to end, over in-memory streams.</summary>
public sealed class LspServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task A_new_type_gets_a_diagnostic_a_lens_and_a_quick_fix_that_moves_it()
    {
        var fixture = await ScannedFixtures.ScanAsync(Engines.Fixture);
        using var repository = fixture.Repository;
        var path = repository.Directory.Write("src/Foo/Pricing/TaxRule.cs", "namespace Foo.Pricing\n{\n    public sealed class TaxRule\n    {\n        public decimal Apply(decimal amount, decimal rate) => amount * (1 + rate);\n    }\n}\n");
        var uri = new Uri(path).AbsoluteUri;
        await using var client = new TestClient(fixture.Root);

        var initialize = await client.RequestAsync("initialize", new JsonObject
        {
            ["rootUri"] = new Uri(fixture.Root).AbsoluteUri,
            ["capabilities"] = new JsonObject
            {
                ["window"] = new JsonObject { ["workDoneProgress"] = true, ["showDocument"] = new JsonObject { ["support"] = true } },
            },
            ["initializationOptions"] = new JsonObject { ["enabled"] = "auto", ["codeLens"] = true },
        });
        await client.NotifyAsync("initialized", new JsonObject());
        var status = await client.NextAsync("offramp/status");
        await client.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "csharp", ["version"] = 1, ["text"] = File.ReadAllText(path) },
        });
        var published = await client.NextAsync("textDocument/publishDiagnostics", p => (string?)p["uri"] == uri && p["diagnostics"]!.AsArray().Count > 0);
        var lenses = (await client.RequestAsync("textDocument/codeLens", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }))!.AsArray();
        var diagnostic = published["diagnostics"]!.AsArray().Single()!.AsObject();
        var actions = (await client.RequestAsync("textDocument/codeAction", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = diagnostic["range"]!.DeepClone(),
            ["context"] = new JsonObject { ["diagnostics"] = new JsonArray(diagnostic.DeepClone()) },
        }))!.AsArray();
        var quickFix = actions.Single(a => (string?)a!["kind"] == "quickfix")!;
        var move = await client.RequestAsync("workspace/executeCommand", quickFix["command"]!.DeepClone().AsObject());
        await client.RequestAsync("shutdown", null);
        await client.NotifyAsync("exit", null);

        Assert.Equal(new JsonArray("offramp.move", "offramp.scan", "offramp.refresh").ToJsonString(), initialize!["capabilities"]!["executeCommandProvider"]!["commands"]!.ToJsonString());
        Assert.Equal((true, "fresh"), ((bool)status["enabled"]!, (string)status["model"]!));
        Assert.Equal(("OFR6001", 3, 2, 24), ((string)diagnostic["code"]!, (int)diagnostic["severity"]!, (int)diagnostic["range"]!["start"]!["line"]!, (int)diagnostic["range"]!["start"]!["character"]!));
        Assert.Equal(["Offramp: move to ModernF", "Offramp: move to Shared"], lenses.Select(l => (string)l!["command"]!["title"]!));
        Assert.Equal("Move TaxRule.cs to ModernF (Offramp)", (string)quickFix["title"]!);
        Assert.Contains("Move TaxRule.cs to ModernF?", client.Asked.Single(), StringComparison.Ordinal);
        Assert.True((bool)move!["apply"]!["applied"]!);
        Assert.True(repository.Directory.Exists("src/ModernF/Pricing/TaxRule.cs"));
        Assert.Contains(client.Shown, s => s.StartsWith("Moved TaxRule.cs to ModernF", StringComparison.Ordinal));
        Assert.Equal(new Uri(repository.Directory.Combine("src", "ModernF", "Pricing", "TaxRule.cs")).AbsoluteUri, client.Opened.Single());
        Assert.Equal(0, await client.ServerExit.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Turned_off_it_answers_with_nothing_and_a_file_report_is_available_on_request()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        var uri = new Uri(fixture.Repository.Directory.Combine("src", "Foo", "Pricing", "PriceCalculator.cs")).AbsoluteUri;
        await using var off = new TestClient(fixture.Root);
        await using var on = new TestClient(fixture.Root);

        await off.RequestAsync("initialize", new JsonObject { ["rootUri"] = new Uri(fixture.Root).AbsoluteUri, ["initializationOptions"] = new JsonObject { ["enabled"] = "off", ["clientCommands"] = true } });
        await off.NotifyAsync("initialized", new JsonObject());
        var offStatus = await off.NextAsync("offramp/status");
        var offLenses = await off.RequestAsync("textDocument/codeLens", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
        var initialize = await on.RequestAsync("initialize", new JsonObject
        {
            ["rootUri"] = new Uri(fixture.Root).AbsoluteUri,
            ["initializationOptions"] = new JsonObject
            {
                ["clientCommands"] = true,
                ["projectMap"] = new JsonArray(new JsonObject { ["from"] = "Legacy", ["to"] = "Nope" }),
            },
        });
        await on.NotifyAsync("initialized", new JsonObject());
        var onStatus = await on.NextAsync("offramp/status");
        var report = await on.RequestAsync("offramp/fileReport", new JsonObject { ["uri"] = uri });
        var unknown = await Assert.ThrowsAsync<LspException>(() => on.RequestAsync("textDocument/hover", new JsonObject()));

        Assert.Equal((false, "setting-off"), ((bool)offStatus["enabled"]!, (string)offStatus["reason"]!));
        Assert.Empty(offLenses!.AsArray());
        Assert.Null(initialize!["capabilities"]!["executeCommandProvider"]);
        Assert.Equal("OFR6002", (string?)onStatus["problems"]![0]!["code"]);
        Assert.Equal("src/Foo/Pricing/PriceCalculator.cs", (string?)report!["file"]);
        Assert.True((bool)report["moves"]![0]!["movable"]!);
        Assert.Equal(-32601, unknown.Code);
    }

    [Fact]
    public async Task A_settings_change_while_the_client_is_asked_to_refresh_lenses_does_not_block_the_server()
    {
        var fixture = await ScannedFixtures.GetAsync(Engines.Fixture);
        var uri = new Uri(fixture.Repository.Directory.Combine("src", "Foo", "Pricing", "PriceCalculator.cs")).AbsoluteUri;
        await using var client = new TestClient(fixture.Root);

        // VS Code sends didChangeConfiguration right after initialized and supports codeLens/refresh, which the
        // server asks for after loading; an answer the read loop cannot read would stall every later request.
        await client.RequestAsync("initialize", new JsonObject
        {
            ["rootUri"] = new Uri(fixture.Root).AbsoluteUri,
            ["capabilities"] = new JsonObject { ["workspace"] = new JsonObject { ["codeLens"] = new JsonObject { ["refreshSupport"] = true } } },
            ["initializationOptions"] = new JsonObject { ["clientCommands"] = true },
        });
        await client.NotifyAsync("initialized", new JsonObject());
        await client.NotifyAsync("workspace/didChangeConfiguration", new JsonObject { ["settings"] = new JsonObject { ["offramp"] = new JsonObject { ["codeLens"] = true } } });
        await client.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "csharp", ["version"] = 1, ["text"] = fixture.Repository.Directory.Read("src/Foo/Pricing/PriceCalculator.cs") },
        });
        await client.NextAsync("offramp/status");
        await client.NextAsync("offramp/status");
        var lenses = await client.RequestAsync("textDocument/codeLens", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });

        Assert.Equal(2, lenses!.AsArray().Count);
        Assert.True(client.Refreshes > 0);
    }

    /// <summary>A language client over in-memory pipes; it answers the server's requests (Move, progress, showDocument).</summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();
        private readonly LspConnection _connection;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _responses = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(string Method, JsonObject Params)> _notifications = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly Task _reader;
        private int _id;
        private int _refreshes;

        public TestClient(string root)
        {
            _connection = new LspConnection(_toClient.Reader.AsStream(), _toServer.Writer.AsStream());
            ServerExit = OfframpLanguageServer.RunAsync(_toServer.Reader.AsStream(), _toClient.Writer.AsStream(), new IdeServerOptions
            {
                Processes = ProcessRunner.Instance,
                Git = new GitService(ProcessRunner.Instance),
                ScanAsync = (_, _, _) => Task.FromResult(0),
                Debounce = TimeSpan.FromMilliseconds(10),
            }, _stop.Token);
            _ = root;
            _reader = Task.Run(ReadAsync);
        }

        public Task<int> ServerExit { get; }

        public int Refreshes => Volatile.Read(ref _refreshes);

        public ConcurrentQueue<string> Asked { get; } = new();

        public ConcurrentQueue<string> Shown { get; } = new();

        public ConcurrentQueue<string> Opened { get; } = new();

        public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters)
        {
            var id = Interlocked.Increment(ref _id);
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = completion;
            await _connection.WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, CancellationToken.None);
            var response = await completion.Task.WaitAsync(Timeout);
            if (response["error"] is JsonObject error)
            {
                throw new LspException((int)error["code"]!, (string)error["message"]!);
            }

            return response["result"];
        }

        public Task NotifyAsync(string method, JsonObject? parameters) =>
            _connection.WriteAsync(parameters is null ? new JsonObject { ["method"] = method } : new JsonObject { ["method"] = method, ["params"] = parameters }, CancellationToken.None);

        /// <summary>The next notification of a method (matching a condition); earlier ones of other kinds are kept.</summary>
        public async Task<JsonObject> NextAsync(string method, Func<JsonObject, bool>? condition = null)
        {
            var deadline = DateTime.UtcNow + Timeout;
            var seen = new List<(string, JsonObject)>();
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    while (_notifications.TryDequeue(out var notification))
                    {
                        if (notification.Method == method && (condition?.Invoke(notification.Params) ?? true))
                        {
                            return notification.Params;
                        }

                        seen.Add(notification);
                    }

                    await _arrived.WaitAsync(TimeSpan.FromSeconds(1));
                }
            }
            finally
            {
                foreach (var notification in seen)
                {
                    _notifications.Enqueue(notification);
                }
            }

            throw new TimeoutException($"No {method} notification arrived.");
        }

        private async Task ReadAsync()
        {
            while (await _connection.ReadAsync(_stop.Token) is { } message)
            {
                var method = (string?)message["method"];
                if (method is null)
                {
                    if (_responses.TryRemove(message["id"]!.ToJsonString().Trim('"'), out var completion))
                    {
                        completion.TrySetResult(message);
                    }

                    continue;
                }

                var parameters = message["params"] as JsonObject ?? [];
                if (message["id"] is { } id)
                {
                    if (method == "workspace/codeLens/refresh")
                    {
                        Interlocked.Increment(ref _refreshes);
                    }

                    JsonNode? result = method switch
                    {
                        "window/showMessageRequest" => Ask(parameters),
                        "window/showDocument" => Open(parameters),
                        _ => null,
                    };
                    await _connection.WriteAsync(new JsonObject { ["id"] = id.DeepClone(), ["result"] = result }, CancellationToken.None);
                    continue;
                }

                if (method == "window/showMessage")
                {
                    Shown.Enqueue((string)parameters["message"]!);
                }

                _notifications.Enqueue((method, parameters));
                _arrived.Release();
            }
        }

        private JsonNode? Ask(JsonObject parameters)
        {
            Asked.Enqueue((string)parameters["message"]!);
            return parameters["actions"]![0]!.DeepClone();
        }

        private JsonObject Open(JsonObject parameters)
        {
            Opened.Enqueue((string)parameters["uri"]!);
            return new JsonObject { ["success"] = true };
        }

        public async ValueTask DisposeAsync()
        {
            await _toServer.Writer.CompleteAsync();
            try
            {
                await ServerExit.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }

            await _stop.CancelAsync();
            await _toClient.Writer.CompleteAsync();
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
            {
            }

            _connection.Dispose();
            _stop.Dispose();
            _arrived.Dispose();
        }
    }
}
