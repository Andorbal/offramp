using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Offramp.Mcp.Tests;

/// <summary>
/// Records every progress notification of a client session. The SDK client removes a call's own
/// progress handler as soon as the call's response is processed, and it dispatches incoming
/// messages concurrently, so a notification the server wrote before the response can be handled
/// after that handler is gone and be dropped. A handler registered for the whole session sees
/// them all. Passed as the call's <see cref="IProgress{T}"/> only so the request carries a
/// progress token.
/// </summary>
internal sealed class ProgressListener : IProgress<ProgressNotificationValue>, IAsyncDisposable
{
    private readonly List<ProgressNotificationValue> values = [];
    private readonly IAsyncDisposable registration;

    public ProgressListener(McpClient client) =>
        registration = client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (notification, _) =>
        {
            var value = notification.Params.Deserialize<ProgressNotificationParams>(McpJsonUtilities.DefaultOptions)!.Progress;
            lock (values)
            {
                values.Add(value);
            }

            return default;
        });

    /// <summary>The notifications received so far, once <paramref name="complete"/> holds for them or after ten seconds.</summary>
    public async Task<List<ProgressNotificationValue>> WaitAsync(Func<List<ProgressNotificationValue>, bool> complete, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            List<ProgressNotificationValue> received;
            lock (values)
            {
                received = [.. values];
            }

            if (complete(received) || DateTime.UtcNow > deadline)
            {
                return received;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    void IProgress<ProgressNotificationValue>.Report(ProgressNotificationValue value)
    {
    }

    public ValueTask DisposeAsync() => registration.DisposeAsync();
}
