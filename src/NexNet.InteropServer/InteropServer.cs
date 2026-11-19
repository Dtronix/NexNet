using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.Uds;
using NexNet.Transports.WebSocket;

namespace NexNet.InteropServer;

/// <summary>
/// One <see cref="NexusServer{TServerNexus,TClientProxy}"/> exposing every <see cref="InteropEndpointKind"/> at once:
/// TCP (primary), TLS, UDS, QUIC (where msquic is available), and HttpSocket and WebSocket over HTTP and HTTPS through
/// its <see cref="InteropServerHost"/>. Created by <see cref="InteropServerHost.StartAsync{TServerNexus,TClientProxy}"/>
/// or <see cref="InteropServerHost.AddServerAsync{TServerNexus,TClientProxy}"/>.
/// </summary>
/// <remarks>
/// NexNet does not report the port a TCP listener bound, so the TCP and TLS listeners do not bind port 0: the first
/// start picks free ports and retries with new ones while a port turns out to be taken. Restarts keep the ports.
/// Kestrel and the QUIC listener bind port 0; QUIC keeps the port it was given on restarts.
/// </remarks>
public sealed class InteropServer<TServerNexus, TClientProxy> : IAsyncDisposable, IInteropServerLifetime
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private const int MaxBindAttempts = 5;

    private readonly bool _ownsHost;
    private readonly string _udsDirectory;
    private readonly IReadOnlyDictionary<InteropEndpointKind, ServerConfig> _configs;
    private readonly IList<string> _allowedOrigins;
    private bool _portsAssigned;
    private int _disposed;

    /// <summary>
    /// The host serving this server's HttpSocket and WebSocket endpoints.
    /// </summary>
    public InteropServerHost Host { get; }

    /// <summary>
    /// The NexNet server.
    /// </summary>
    public NexusServer<TServerNexus, TClientProxy> Server { get; }

    /// <summary>
    /// Shared-host routing key; null when served at the root paths.
    /// </summary>
    public string? TestId { get; }

    /// <summary>
    /// Where each endpoint listens. Set once the server has started the first time.
    /// </summary>
    public InteropEndpoints Endpoints { get; private set; } = null!;

    /// <summary>
    /// Server configuration of each endpoint.
    /// </summary>
    public IReadOnlyDictionary<InteropEndpointKind, ServerConfig> Configs => _configs;

    internal InteropServer(
        InteropServerHost host,
        InteropServerOptions options,
        string pathPrefix,
        Func<TServerNexus> nexusFactory,
        Action<TServerNexus>? configureCollections,
        bool ownsHost)
    {
        Host = host;
        TestId = options.TestId;
        _ownsHost = ownsHost;
        _allowedOrigins = options.AllowedOrigins.ToList();

        var udsParent = options.UdsDirectory ?? Path.GetTempPath();
        _udsDirectory = Path.Combine(udsParent, "nnis-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_udsDirectory);

        var webSocket = new WebSocketServerConfig { Path = pathPrefix + "/ws" };
        var webSocketTls = new WebSocketServerConfig { Path = pathPrefix + "/wss" };
        foreach (var origin in _allowedOrigins)
        {
            webSocket.AllowedOrigins.Add(origin);
            webSocketTls.AllowedOrigins.Add(origin);
        }

        var configs = new Dictionary<InteropEndpointKind, ServerConfig>
        {
            [InteropEndpointKind.Tcp] = new TcpServerConfig
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                TcpNoDelay = true,
            },
            [InteropEndpointKind.TcpTls] = new TcpTlsServerConfig
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                TcpNoDelay = true,
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = host.ServerCertificate,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
            },
            [InteropEndpointKind.Uds] = new UdsServerConfig
            {
                EndPoint = new UnixDomainSocketEndPoint(Path.Combine(_udsDirectory, "s")),
            },
            [InteropEndpointKind.HttpSocket] = new HttpSocketServerConfig { Path = pathPrefix + "/hs" },
            [InteropEndpointKind.HttpSocketTls] = new HttpSocketServerConfig { Path = pathPrefix + "/hss" },
            [InteropEndpointKind.WebSocket] = webSocket,
            [InteropEndpointKind.WebSocketTls] = webSocketTls,
        };

        // QUIC is C#-only (no other client language implements it) and needs msquic; without it the server has no
        // QUIC endpoint and matrix rows over QUIC are skipped.
        if (InteropEndpointKinds.IsQuicSupported)
        {
            configs[InteropEndpointKind.Quic] = new QuicServerConfig
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = host.ServerCertificate,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
            };
        }

        var tokenValidator = options.TokenValidator;
        foreach (var (kind, config) in configs)
        {
            config.Logger = options.Logger;
            config.Authenticate = tokenValidator != null;
            if (options.EndpointTimeouts.TryGetValue(kind, out var timeout))
                config.Timeout = timeout;
            else if (options.Timeout is { } defaultTimeout)
                config.Timeout = defaultTimeout;
            options.ConfigureEndpoint?.Invoke(kind, config);
        }

        _configs = configs;

        Server = new NexusServer<TServerNexus, TClientProxy>(
            configs[InteropEndpointKind.Tcp],
            () =>
            {
                var nexus = nexusFactory();
                if (nexus is IInteropTokenValidatingNexus validating)
                    validating.TokenValidator = tokenValidator;
                return nexus;
            },
            configureCollections);

        Server.AddListener(configs[InteropEndpointKind.TcpTls], InteropEndpointKind.TcpTls.GetEndpointName());
        Server.AddListener(configs[InteropEndpointKind.Uds], InteropEndpointKind.Uds.GetEndpointName());
        if (configs.TryGetValue(InteropEndpointKind.Quic, out var quic))
            Server.AddListener(quic, InteropEndpointKind.Quic.GetEndpointName());
    }

    /// <summary>
    /// Builds the request pipeline of the plain HTTP (<paramref name="secure"/> false) or HTTPS listener, adding this
    /// server's HttpSocket and WebSocket endpoints for it.
    /// </summary>
    internal RoutePipeline BuildRoutes(bool secure)
    {
        var httpSocketKind = secure ? InteropEndpointKind.HttpSocketTls : InteropEndpointKind.HttpSocket;
        var webSocketKind = secure ? InteropEndpointKind.WebSocketTls : InteropEndpointKind.WebSocket;

        return new RoutePipeline(app =>
        {
            var httpSocketOptions = new HttpSocketOptions();
            httpSocketOptions.AllowedOrigins.AddRange(_allowedOrigins);
            app.UseHttpSockets(httpSocketOptions);
            app.MapNexusHttpSocket(Server, (HttpSocketServerConfig)_configs[httpSocketKind], httpSocketKind.GetEndpointName());
            app.MapNexusWebSocket(Server, (WebSocketServerConfig)_configs[webSocketKind], webSocketKind.GetEndpointName());
        });
    }

    /// <summary>
    /// Starts the server: the first time on newly picked TCP and TLS ports, afterwards on the same ones.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var tcp = (TcpServerConfig)_configs[InteropEndpointKind.Tcp];
        var tls = (TcpServerConfig)_configs[InteropEndpointKind.TcpTls];

        for (var attempt = 1; ; attempt++)
        {
            if (!_portsAssigned)
            {
                tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort());
                tls.EndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort());
            }

            try
            {
                await Server.StartAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (TransportException e) when (e.Error == TransportError.AddressInUse
                                               && !_portsAssigned
                                               && attempt < MaxBindAttempts)
            {
                // Another process took a picked port between the probe and the bind; pick new ones.
            }
        }

        if (!_portsAssigned)
        {
            _portsAssigned = true;
            IPEndPoint? quicEndPoint = null;
            if (_configs.TryGetValue(InteropEndpointKind.Quic, out var quicConfig))
            {
                // Bound on port 0: keep the picked port for restarts.
                var quicName = InteropEndpointKind.Quic.GetEndpointName();
                quicEndPoint = Server.Endpoints.Single(e => e.Name == quicName).BoundEndPoint as IPEndPoint
                               ?? throw new InvalidOperationException("The QUIC listener reported no bound end point.");
                ((QuicServerConfig)quicConfig).EndPoint = quicEndPoint;
            }

            Endpoints = new InteropEndpoints(
                (IPEndPoint)tcp.EndPoint,
                (IPEndPoint)tls.EndPoint,
                ((UdsServerConfig)_configs[InteropEndpointKind.Uds]).EndPoint.ToString(),
                AspUri(Host.HttpBaseUri, InteropEndpointKind.HttpSocket, "http"),
                AspUri(Host.HttpsBaseUri, InteropEndpointKind.HttpSocketTls, "https"),
                AspUri(Host.HttpBaseUri, InteropEndpointKind.WebSocket, "ws"),
                AspUri(Host.HttpsBaseUri, InteropEndpointKind.WebSocketTls, "wss"),
                quicEndPoint);
        }
    }

    /// <summary>
    /// Stops the server. It can be started again with <see cref="StartAsync"/> on the same endpoints.
    /// </summary>
    public Task StopAsync() => Server.StopAsync();

    private Uri AspUri(Uri baseUri, InteropEndpointKind kind, string scheme)
        => new UriBuilder(baseUri) { Scheme = scheme, Path = ((AspServerConfig)_configs[kind]).Path }.Uri;

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Creates a C# client configuration that connects to <paramref name="kind"/>. TLS endpoints are validated
    /// against <paramref name="trustedRoot"/> (default: the F08 test CA) with host name verification.
    /// </summary>
    /// <exception cref="NotSupportedException">The server has no endpoint of <paramref name="kind"/> (QUIC without msquic).</exception>
    /// <param name="kind">The endpoint to connect to.</param>
    /// <param name="trustedRoot">The only root CA the client trusts; null for the test CA.</param>
    public ClientConfig CreateClientConfig(InteropEndpointKind kind, X509Certificate2? trustedRoot = null)
    {
        if (Endpoints == null)
            throw new InvalidOperationException("Start the server first.");

        SslClientAuthenticationOptions Tls(string? targetHost) => new()
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false,
            RemoteCertificateValidationCallback =
                InteropCertificates.CreateValidationCallback(trustedRoot ?? InteropCertificates.LoadTestCa()),
        };

        return kind switch
        {
            InteropEndpointKind.Tcp => new TcpClientConfig { EndPoint = Endpoints.Tcp },
            InteropEndpointKind.TcpTls => new TcpTlsClientConfig
            {
                EndPoint = Endpoints.TcpTls,
                SslClientAuthenticationOptions = Tls("localhost"),
            },
            InteropEndpointKind.Uds => new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(Endpoints.UdsPath) },
            InteropEndpointKind.HttpSocket => new HttpSocketClientConfig { Url = Endpoints.HttpSocket },
            InteropEndpointKind.HttpSocketTls => new HttpSocketClientConfig
            {
                Url = Endpoints.HttpSocketTls,
                SslClientAuthenticationOptions = Tls(null),
            },
            InteropEndpointKind.WebSocket => new WebSocketClientConfig { Url = Endpoints.WebSocket },
            InteropEndpointKind.WebSocketTls => new WebSocketClientConfig
            {
                Url = Endpoints.WebSocketTls,
                SslClientAuthenticationOptions = Tls(null),
            },
            InteropEndpointKind.Quic => new QuicClientConfig
            {
                EndPoint = Endpoints.Quic ?? throw new NotSupportedException(
                    "The server has no QUIC endpoint: " + (kind.GetUnsupportedReason() ?? "it was not added.")),
                // msquic needs a DNS server name (SNI) for the handshake.
                SslClientAuthenticationOptions = Tls("localhost"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    /// Stops the server, removes it from its host and deletes its socket folder. A server started with
    /// <see cref="InteropServerHost.StartAsync{TServerNexus,TClientProxy}"/> disposes its host too.
    /// </summary>
    public ValueTask DisposeAsync() => DisposeCoreAsync(disposeHost: true);

    async ValueTask IInteropServerLifetime.DisposeCoreAsync(bool disposeHost) => await DisposeCoreAsync(disposeHost).ConfigureAwait(false);

    internal async ValueTask DisposeCoreAsync(bool disposeHost)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (Server.State != NexusServerState.Stopped)
                await Server.StopAsync().ConfigureAwait(false);
            await Server.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await Host.RemoveRoutesAsync(TestId).ConfigureAwait(false);
            try
            {
                Directory.Delete(_udsDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A socket file still held open; the temp folder is cleaned up eventually.
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (disposeHost && _ownsHost)
                await Host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
