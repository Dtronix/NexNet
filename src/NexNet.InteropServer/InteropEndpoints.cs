using System.Net;

namespace NexNet.InteropServer;

/// <summary>
/// Where each endpoint of an interop server listens. Every address uses <c>127.0.0.1</c>; the test certificate
/// covers it (and <c>localhost</c>).
/// </summary>
/// <param name="Tcp">TCP listener.</param>
/// <param name="TcpTls">TLS over TCP listener.</param>
/// <param name="UdsPath">Unix domain socket file path.</param>
/// <param name="HttpSocket">HttpSocket URL (<c>http://</c>).</param>
/// <param name="HttpSocketTls">HttpSocket URL (<c>https://</c>).</param>
/// <param name="WebSocket">WebSocket URL (<c>ws://</c>).</param>
/// <param name="WebSocketTls">WebSocket URL (<c>wss://</c>).</param>
public sealed record InteropEndpoints(
    IPEndPoint Tcp,
    IPEndPoint TcpTls,
    string UdsPath,
    Uri HttpSocket,
    Uri HttpSocketTls,
    Uri WebSocket,
    Uri WebSocketTls)
{
    /// <summary>
    /// The address of <paramref name="kind"/> as one string: <c>tcp://127.0.0.1:port</c>,
    /// <c>tls://127.0.0.1:port</c>, <c>uds:</c> followed by the socket path, or the HTTP/WebSocket URL.
    /// </summary>
    public string GetAddress(InteropEndpointKind kind) => kind switch
    {
        InteropEndpointKind.Tcp => $"tcp://{Tcp}",
        InteropEndpointKind.TcpTls => $"tls://{TcpTls}",
        InteropEndpointKind.Uds => "uds:" + UdsPath,
        InteropEndpointKind.HttpSocket => HttpSocket.ToString(),
        InteropEndpointKind.HttpSocketTls => HttpSocketTls.ToString(),
        InteropEndpointKind.WebSocket => WebSocket.ToString(),
        InteropEndpointKind.WebSocketTls => WebSocketTls.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Every endpoint's address (<see cref="GetAddress"/>), keyed by kind.
    /// </summary>
    public IReadOnlyDictionary<InteropEndpointKind, string> ToDictionary()
        => InteropEndpointKinds.All.ToDictionary(k => k, GetAddress);
}
