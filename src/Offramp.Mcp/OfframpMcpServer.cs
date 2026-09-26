using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Offramp.Mcp;

/// <summary>
/// The Model Context Protocol server (docs/spec/commands/mcp-and-llm.md#mcp-serve) over stdio,
/// with the low-level handlers of the official SDK: tools and resources come from an
/// <see cref="McpCatalog"/>, and a call's progress becomes <c>notifications/progress</c> for
/// the request's progress token.
/// </summary>
public static class OfframpMcpServer
{
    /// <summary>Serves on the process's stdin and stdout until the client disconnects.</summary>
    public static async Task RunAsync(McpCatalog catalog, CancellationToken cancellationToken)
    {
        var options = Options(catalog);
        await using var transport = new StdioServerTransport(options, NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, serviceProvider: null);
        await server.RunAsync(cancellationToken);
    }

    /// <summary>Serves on the given streams (tests, embedding) until the client disconnects.</summary>
    public static async Task RunAsync(McpCatalog catalog, Stream input, Stream output, CancellationToken cancellationToken)
    {
        var options = Options(catalog);
        await using var transport = new StreamServerTransport(input, output, catalog.ServerName, NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, serviceProvider: null);
        await server.RunAsync(cancellationToken);
    }

    public static McpServerOptions Options(McpCatalog catalog) => new()
    {
        ServerInfo = new Implementation { Name = catalog.ServerName, Version = catalog.ServerVersion },
        ServerInstructions = catalog.Instructions,
        Capabilities = new ServerCapabilities { Tools = new ToolsCapability(), Resources = new ResourcesCapability() },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [.. catalog.Tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(t.InputSchema, McpJson.Default.JsonObject) })],
            }),
            CallToolHandler = (context, cancellationToken) => CallAsync(catalog, context, cancellationToken),
            ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = [.. catalog.Resources().Select(r => new Resource { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = r.MimeType })],
            }),
            ListResourceTemplatesHandler = (_, _) => ValueTask.FromResult(new ListResourceTemplatesResult
            {
                ResourceTemplates = [.. catalog.ResourceTemplates.Select(r => new ResourceTemplate { UriTemplate = r.UriTemplate, Name = r.Name, Description = r.Description, MimeType = r.MimeType })],
            }),
            ReadResourceHandler = async (context, cancellationToken) =>
            {
                var uri = context.Params?.Uri ?? "";
                var read = await catalog.ReadResourceAsync(uri, cancellationToken)
                    ?? throw new McpException($"There is no resource {uri}.");
                return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = read.MimeType, Text = read.Text }] };
            },
        },
    };

    private static async ValueTask<CallToolResult> CallAsync(McpCatalog catalog, RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name ?? "";
        var tool = catalog.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"There is no tool {name}; offramp_help lists them." }] };
        }

        var token = context.Params?.ProgressToken;
        var server = context.Server;
        IProgress<McpProgress> progress = token is { } progressToken
            ? new NotifyingProgress(server, progressToken, cancellationToken)
            : new Progress<McpProgress>(_ => { });
        var arguments = context.Params?.Arguments?.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        var output = await tool.InvokeAsync(new McpToolCall(arguments, progress), cancellationToken);
        return new CallToolResult { IsError = output.IsError, Content = [.. output.Texts.Select(t => (ContentBlock)new TextContentBlock { Text = t })] };
    }

    /// <summary>Sends each report as it happens; a notification that fails to send is dropped (progress is advisory).</summary>
    private sealed class NotifyingProgress(McpServer server, ProgressToken token, CancellationToken cancellationToken) : IProgress<McpProgress>
    {
        public void Report(McpProgress value)
        {
            try
            {
                server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = (float)value.Progress, Total = (float?)value.Total, Message = value.Message },
                    cancellationToken: cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
internal sealed partial class McpJson : System.Text.Json.Serialization.JsonSerializerContext;
