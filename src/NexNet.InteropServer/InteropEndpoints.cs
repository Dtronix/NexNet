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
/// <param name="Quic">
/// QUIC listener (UDP); null when the server has no QUIC endpoint (msquic unavailable,
/// <see cref="InteropEndpointKinds.IsQuicSupported"/>).
/// </param>
public sealed record InteropEndpoints(
    IPEndPoint Tcp,
    IPEndPoint TcpTls,
    string UdsPath,
    Uri HttpSocket,
    Uri HttpSocketTls,
    Uri WebSocket,
    Uri WebSocketTls,
    IPEndPoint? Quic = null)
{
    /// <summary>True when the server has an endpoint of <paramref name="kind"/>.</summary>
    public bool Has(InteropEndpointKind kind) => kind != InteropEndpointKind.Quic || Quic != null;

    /// <summary>
    /// The address of <paramref name="kind"/> as one string: <c>tcp://127.0.0.1:port</c>,
    /// <c>tls://127.0.0.1:port</c>, <c>uds:</c> followed by the socket path, the HTTP/WebSocket URL, or
    /// <c>quic://127.0.0.1:port</c>.
    /// </summary>
    /// <exception cref="NotSupportedException">The server has no endpoint of <paramref name="kind"/> (<see cref="Has"/>).</exception>
    public string GetAddress(InteropEndpointKind kind) => kind switch
    {
        InteropEndpointKind.Tcp => $"tcp://{Tcp}",
        InteropEndpointKind.TcpTls => $"tls://{TcpTls}",
        InteropEndpointKind.Uds => "uds:" + UdsPath,
        InteropEndpointKind.HttpSocket => HttpSocket.ToString(),
        InteropEndpointKind.HttpSocketTls => HttpSocketTls.ToString(),
        InteropEndpointKind.WebSocket => WebSocket.ToString(),
        InteropEndpointKind.WebSocketTls => WebSocketTls.ToString(),
        InteropEndpointKind.Quic => Quic != null
            ? $"quic://{Quic}"
            : throw new NotSupportedException(
                "The server has no QUIC endpoint: " + (InteropEndpointKind.Quic.GetUnsupportedReason() ?? "it was not added.")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Every endpoint's address (<see cref="GetAddress"/>), keyed by kind; kinds the server lacks (<see cref="Has"/>)
    /// are left out.
    /// </summary>
    public IReadOnlyDictionary<InteropEndpointKind, string> ToDictionary()
        => InteropEndpointKinds.All.Where(Has).ToDictionary(k => k, GetAddress);
}
