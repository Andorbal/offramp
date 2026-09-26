using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Offramp.Mcp.Tests;

/// <summary>The catalog-to-protocol mapping, over in-memory pipes: tools, arguments, progress, errors, resources.</summary>
public sealed class ServerTests
{
    [Fact]
    public async Task Tools_and_resources_map_to_the_protocol()
    {
        var echo = new McpToolDefinition("echo", "Echoes its word.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["word"] = new JsonObject { ["type"] = "string" } } },
            (call, _) =>
            {
                call.Progress.Report(new McpProgress(1, 2, "half"));
                call.Progress.Report(new McpProgress(2, 2, "done"));
                return Task.FromResult(new McpToolOutput([call.Arguments["word"].GetString()!, "second block"], IsError: false));
            });
        var catalog = new McpCatalog
        {
            ServerName = "test",
            ServerVersion = "1.0.0",
            Instructions = "Be kind.",
            Tools = [echo],
            Resources = () => [new McpResourceDefinition("test://a", "A", "The letter a.", "text/plain")],
            ResourceTemplates = [new McpResourceTemplateDefinition("test://{x}", "X", "Any letter.", "text/plain")],
            ReadResourceAsync = (uri, _) => Task.FromResult<(string, string)?>(uri == "test://a" ? ("a", "text/plain") : null),
        };
        var toServer = new Pipe();
        var toClient = new Pipe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var server = OfframpMcpServer.RunAsync(catalog, toServer.Reader.AsStream(), toClient.Writer.AsStream(), stop.Token);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(("test", "1.0.0", "Be kind."), (client.ServerInfo.Name, client.ServerInfo.Version, client.ServerInstructions));
            var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("echo", tool.Name);
            Assert.Equal("string", tool.JsonSchema.GetProperty("properties").GetProperty("word").GetProperty("type").GetString());

            var progress = new List<ProgressNotificationValue>();
            var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["word"] = "hello" }, new Collect(progress), cancellationToken: TestContext.Current.CancellationToken);
            var unknown = await client.CallToolAsync("nope", cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(true, result.IsError);
            Assert.Equal(["hello", "second block"], result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            for (var i = 0; i < 50 && progress.Count < 2; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Equal(["done", "half"], progress.Select(p => p.Message).Order(StringComparer.Ordinal));
            Assert.True(unknown.IsError);
            Assert.Contains("offramp_help", unknown.Content.OfType<TextContentBlock>().Single().Text, StringComparison.Ordinal);

            Assert.Equal("test://a", Assert.Single(await client.ListResourcesAsync(cancellationToken: TestContext.Current.CancellationToken)).Uri);
            Assert.Equal("test://{x}", Assert.Single(await client.ListResourceTemplatesAsync(cancellationToken: TestContext.Current.CancellationToken)).UriTemplate);
            var read = await client.ReadResourceAsync("test://a", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("a", read.Contents.OfType<TextResourceContents>().Single().Text);
            await Assert.ThrowsAsync<McpProtocolException>(async () => await client.ReadResourceAsync("test://b", cancellationToken: TestContext.Current.CancellationToken));
        }

        await stop.CancelAsync();
        await toServer.Writer.CompleteAsync();
        try
        {
            await server;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class Collect(List<ProgressNotificationValue> values) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }
}
