using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Offramp.Ide.Lsp;

/// <summary>
/// JSON-RPC 2.0 messages framed the Language Server Protocol way (<c>Content-Length</c> headers,
/// UTF-8 bodies) over a pair of streams. Writes are serialized; reads happen on one loop.
/// </summary>
public sealed class LspConnection(Stream input, Stream output) : IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>The next message, or null at the end of the input.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        var length = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line[(colon + 1)..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        if (length < 0)
        {
            throw new InvalidDataException("A message without Content-Length.");
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await input.ReadAsync(body.AsMemory(read, length - read), cancellationToken);
            if (count == 0)
            {
                return null;
            }

            read += count;
        }

        return JsonNode.Parse(body) as JsonObject;
    }

    public async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        message["jsonrpc"] = "2.0";
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n\r\n"));
        await _write.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(body, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            }

            if (buffer[0] == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.ASCII.GetString([.. bytes]);
            }

            bytes.Add(buffer[0]);
        }
    }

    public void Dispose() => _write.Dispose();
}
