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

    /// <summary>Whether the client is the JavaScript client (Node or a browser).</summary>
    public static bool IsJavaScript(ClientTarget target)
        => target.Language == ClientLanguages.Node || target.Language.StartsWith("browser", StringComparison.Ordinal);

    /// <summary>
    /// The frames the client counts as sent beyond those the server counts as received: the JavaScript client counts the
    /// protocol preamble it writes before its greeting as a frame (<c>WriteQueue.framesSent</c>), which C# and Java do
    /// not. A runtime parity gap with a follow-up card (Q05 report); the bytes match on every language.
    /// </summary>
    public static int PreambleFrames(ClientTarget target) => IsJavaScript(target) ? 1 : 0;

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
