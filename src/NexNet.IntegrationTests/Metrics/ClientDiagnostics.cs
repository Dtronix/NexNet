using NexNet.Interop;

namespace NexNet.IntegrationTests.Metrics;

/// <summary>Helpers of the matrix tests that read a client's snapshot and metrics.</summary>
internal static class ClientDiagnostics
{
    /// <summary>The <c>nexnet.transport</c> tag (and snapshot <c>transport</c>) of a client-matrix transport.</summary>
    public static string TransportTag(ClientTransport transport) => transport switch
    {
        ClientTransport.Tcp => "tcp",
        ClientTransport.TcpTls => "tls",
        ClientTransport.Uds => "uds",
        ClientTransport.WebSocket or ClientTransport.WebSocketTls => "websocket",
        ClientTransport.HttpSocket or ClientTransport.HttpSocketTls => "httpsocket",
        ClientTransport.Quic => "quic",
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
    };

    /// <summary>
    /// Reads <paramref name="read"/> until <paramref name="done"/> holds, for at most <paramref name="seconds"/> (already
    /// scaled: pass <c>T(x)</c>);
    /// returns the last value, so the assertion that follows reports it.
    /// </summary>
    public static async Task<T> Until<T>(Func<Task<T>> read, Func<T, bool> done, double seconds = 2)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        var value = await read().ConfigureAwait(false);
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
            value = await read().ConfigureAwait(false);
        }

        return value;
    }

    /// <summary>The client's snapshot once <paramref name="done"/> holds for it (null never does).</summary>
    public static async Task<NexNet.Diagnostics.NexusSessionSnapshot?> SnapshotUntil(
        IClientUnderTest client, Func<NexNet.Diagnostics.NexusSessionSnapshot, bool> done, double seconds = 2)
        => await Until(client.GetSnapshotAsync, s => s != null && done(s), seconds).ConfigureAwait(false);

    /// <summary>The client's metrics once <paramref name="done"/> holds for them.</summary>
    public static Task<ClientMetrics> MetricsUntil(IClientUnderTest client, Func<ClientMetrics, bool> done, double seconds = 2)
        => Until(client.GetMetricsAsync, done, seconds);
}
