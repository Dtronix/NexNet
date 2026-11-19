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

    /// <summary>
    /// QUIC listener (NexNet.Quic), named <c>quic</c>. Only present where msquic is available
    /// (<see cref="InteropEndpointKinds.IsQuicSupported"/>); C#-only, no other client language implements QUIC.
    /// </summary>
    Quic,
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
        InteropEndpointKind.Quic => "quic",
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
        InteropEndpointKind.Quic => "quic",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// True when the connection is encrypted (TLS, HTTPS, WSS).
    /// </summary>
    public static bool IsSecure(this InteropEndpointKind kind)
        => kind is InteropEndpointKind.TcpTls or InteropEndpointKind.HttpSocketTls or InteropEndpointKind.WebSocketTls
            or InteropEndpointKind.Quic;

    /// <summary>
    /// True when this process can host and connect over QUIC (msquic is available). Interop servers add their
    /// <see cref="InteropEndpointKind.Quic"/> endpoint only then.
    /// </summary>
    public static bool IsQuicSupported { get; } =
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && System.Net.Quic.QuicListener.IsSupported
        && System.Net.Quic.QuicConnection.IsSupported;

    /// <summary>
    /// Why this process cannot use <paramref name="kind"/>; null when it can. Only
    /// <see cref="InteropEndpointKind.Quic"/> depends on the platform (msquic).
    /// </summary>
    public static string? GetUnsupportedReason(this InteropEndpointKind kind)
        => kind == InteropEndpointKind.Quic && !IsQuicSupported
            ? "QUIC (msquic) is not available on this platform."
            : null;

    /// <summary>
    /// True for the endpoints served by Kestrel (HttpSocket and WebSocket).
    /// </summary>
    public static bool IsAsp(this InteropEndpointKind kind)
        => kind is InteropEndpointKind.HttpSocket or InteropEndpointKind.HttpSocketTls
            or InteropEndpointKind.WebSocket or InteropEndpointKind.WebSocketTls;
}
