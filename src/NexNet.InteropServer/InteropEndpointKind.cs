namespace NexNet.InteropServer;

/// <summary>
/// The endpoints an interop server exposes at once. Every kind is a separate NexNet endpoint of the same server, so
/// each has its own name, path and session settings.
/// </summary>
public enum InteropEndpointKind
{
    /// <summary>Plain TCP listener. The server's primary endpoint, named <c>tcp</c>.</summary>
    Tcp,

    /// <summary>TLS over TCP listener, named <c>tcp-tls</c>.</summary>
    TcpTls,

    /// <summary>Unix domain socket listener, named <c>uds</c>.</summary>
    Uds,

    /// <summary>HttpSocket over plain HTTP (Kestrel), named <c>httpsocket</c>.</summary>
    HttpSocket,

    /// <summary>HttpSocket over HTTPS (Kestrel), named <c>httpsocket-tls</c>.</summary>
    HttpSocketTls,

    /// <summary>WebSocket over plain HTTP (<c>ws://</c>, Kestrel), named <c>websocket</c>.</summary>
    WebSocket,

    /// <summary>WebSocket over HTTPS (<c>wss://</c>, Kestrel), named <c>websocket-tls</c>.</summary>
    WebSocketTls,
}

/// <summary>
/// Names and properties of <see cref="InteropEndpointKind"/> values.
/// </summary>
public static class InteropEndpointKinds
{
    /// <summary>
    /// Every endpoint kind, in declaration order.
    /// </summary>
    public static IReadOnlyList<InteropEndpointKind> All { get; } = Enum.GetValues<InteropEndpointKind>();

    /// <summary>
    /// The NexNet endpoint name of <paramref name="kind"/>, as sessions see it in <c>Context.Endpoint.Name</c>.
    /// </summary>
    public static string GetEndpointName(this InteropEndpointKind kind) => kind switch
    {
        InteropEndpointKind.Tcp => "tcp",
        InteropEndpointKind.TcpTls => "tcp-tls",
        InteropEndpointKind.Uds => "uds",
        InteropEndpointKind.HttpSocket => "httpsocket",
        InteropEndpointKind.HttpSocketTls => "httpsocket-tls",
        InteropEndpointKind.WebSocket => "websocket",
        InteropEndpointKind.WebSocketTls => "websocket-tls",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// The NexNet transport name of <paramref name="kind"/>, as in <c>Context.Endpoint.Transport</c>.
    /// </summary>
    public static string GetTransportName(this InteropEndpointKind kind) => kind switch
    {
        InteropEndpointKind.Tcp => "tcp",
        InteropEndpointKind.TcpTls => "tls",
        InteropEndpointKind.Uds => "uds",
        InteropEndpointKind.HttpSocket or InteropEndpointKind.HttpSocketTls => "httpsocket",
        InteropEndpointKind.WebSocket or InteropEndpointKind.WebSocketTls => "websocket",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// True when the connection is encrypted (TLS, HTTPS, WSS).
    /// </summary>
    public static bool IsSecure(this InteropEndpointKind kind)
        => kind is InteropEndpointKind.TcpTls or InteropEndpointKind.HttpSocketTls or InteropEndpointKind.WebSocketTls;

    /// <summary>
    /// True for the endpoints served by Kestrel (HttpSocket and WebSocket).
    /// </summary>
    public static bool IsAsp(this InteropEndpointKind kind)
        => kind is InteropEndpointKind.HttpSocket or InteropEndpointKind.HttpSocketTls
            or InteropEndpointKind.WebSocket or InteropEndpointKind.WebSocketTls;
}
