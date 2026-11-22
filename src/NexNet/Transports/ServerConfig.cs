using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Invocation;
using NexNet.RateLimiting;
using NexNet.Transports.Uds;

namespace NexNet.Transports;

/// <summary>
/// Base configuration file for servers.
/// </summary>
public abstract class ServerConfig : ConfigBase
{
    
    /// <summary>
    /// Gets the connection mode that the server is to operate in.
    /// </summary>
    internal ServerConnectionMode ConnectionMode { get; }
    
    /// <summary>
    /// Option: acceptor backlog size
    /// </summary>
    /// <remarks>
    /// This option will set the listening socket's backlog size
    /// </remarks>
    public int AcceptorBacklog { get; init; } = 20;

    /// <summary>
    /// Set to true to authenticate the client connection.
    /// </summary>
    /// <remarks>
    /// Applies per endpoint. A server whose primary configuration sets it refuses to start while any added endpoint
    /// does not.
    /// </remarks>
    public bool Authenticate { get; set; } = false;

    private int _maxPendingHandshakes = 256;

    /// <summary>
    /// Most connections that may be between accepting the transport and completing authentication at once,
    /// server-wide. New transports are closed while the limit is reached. Bounds concurrent
    /// <c>OnAuthenticate</c> calls and the memory held by unauthenticated connections. 256 default. Must be between
    /// 1 and 100,000.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigBase.HandshakeTimeout"/> must cover the application's <c>OnAuthenticate</c> latency.
    /// </remarks>
    public int MaxPendingHandshakes
    {
        get => _maxPendingHandshakes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(MaxPendingHandshakes));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100_000, nameof(MaxPendingHandshakes));
            _maxPendingHandshakes = value;
        }
    }

    /// <summary>
    /// Configuration for connection rate limiting. Null = disabled (default).
    /// </summary>
    /// <remarks>
    /// Server-wide: only the primary configuration's value is used, and its limits span every endpoint. An endpoint
    /// added with <c>AddListener</c> or <c>AddExternalEndpoint</c> must leave it null.
    /// </remarks>
    public ConnectionRateLimitConfig? RateLimiting { get; set; }

    /// <summary>
    /// Default duration for caching authorization results per session.
    /// Null (default) = caching disabled. Positive values enable caching for that duration.
    /// Can be overridden per-method/collection via <c>NexusAuthorizeAttribute.CacheDurationSeconds</c>.
    /// </summary>
    public TimeSpan? AuthorizationCacheDuration { get; set; }

    /// <summary>
    /// Creates the listener and starts.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Listener interface.</returns>
    protected abstract ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken);

    internal Action? InternalOnConnect = null;

    /// <summary>
    /// Base configuration class for servers, providing options for the server connection
    /// </summary>
    protected ServerConfig(ServerConnectionMode connectionMode)
    {
        ConnectionMode = connectionMode;
    }

    internal ValueTask<ITransportListener?> CreateServerListener(CancellationToken cancellationToken)
    {
        return OnCreateServerListener(cancellationToken);
    }

    /// <summary>
    /// Gets the session manager, creating a default LocalServerSessionManager if none is configured.
    /// </summary>
    /// <returns>The session manager instance.</returns>
    internal IServerSessionManager GetSessionManager()
    {
        return new LocalServerSessionManager();
    }

    /// <summary>
    /// True when the transport encrypts the connection itself (TLS, QUIC).
    /// </summary>
    internal bool IsSecureTransport => TransportName is "tls" or "quic";

    /// <summary>
    /// What an endpoint built from this configuration binds or serves, for snapshots and logs: the socket end point of
    /// a TCP, TLS or UDS listener, the UDP end point of a QUIC listener, the request path of an ASP.NET endpoint; null
    /// when unknown.
    /// </summary>
    internal string? EndpointAddress => this switch
    {
        TcpServerConfig tcp => tcp.EndPoint?.ToString(),
        UdsServerConfig uds => uds.EndPoint?.ToString(),
        AspServerConfig asp => asp.Path,
        _ => UdpEndPoint?.ToString(),
    };

    /// <summary>
    /// The UDP end point a listener built from this configuration binds (QUIC), or null when it binds none. Lets
    /// configurations defined outside this assembly report their address for snapshots and take part in the
    /// port-overlap check when endpoints are added.
    /// </summary>
    internal virtual IPEndPoint? UdpEndPoint => null;

    /// <summary>
    /// Describes why this configuration and <paramref name="other"/> can not both be endpoints of one server (the
    /// same TCP port, UDP port, UDS path or ASP.NET path); null when they can.
    /// </summary>
    internal string? GetEndpointConflict(ServerConfig other)
    {
        if (UdpEndPoint is { } udpA && other.UdpEndPoint is { } udpB && EndPointsOverlap(udpA, udpB))
            return $"UDP end point {udpA} overlaps {udpB}";

        switch (this, other)
        {
            case (TcpServerConfig a, TcpServerConfig b) when EndPointsOverlap(a.EndPoint, b.EndPoint):
                return $"TCP end point {a.EndPoint} overlaps {b.EndPoint}";
            case (UdsServerConfig a, UdsServerConfig b)
                when a.EndPoint != null && string.Equals(a.EndPoint.ToString(), b.EndPoint?.ToString(), StringComparison.Ordinal):
                return $"UDS path {a.EndPoint}";
            case (AspServerConfig a, AspServerConfig b)
                when a.Path != null && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase):
                return $"path {a.Path}";
            default:
                return null;
        }
    }

    private static bool EndPointsOverlap(EndPoint? a, EndPoint? b)
    {
        if (a == null || b == null)
            return false;

        if (a is IPEndPoint ipA && b is IPEndPoint ipB)
        {
            // Port 0 binds an ephemeral port, never one in use.
            if (ipA.Port == 0 || ipA.Port != ipB.Port)
                return false;

            return ipA.Address.Equals(ipB.Address)
                   || IsAnyAddress(ipA.Address)
                   || IsAnyAddress(ipB.Address);
        }

        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnyAddress(IPAddress address)
        => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
}
