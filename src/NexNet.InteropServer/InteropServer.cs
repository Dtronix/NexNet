using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
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
/// NexNet does not report the port a TCP listener bound, so the TCP, TLS and QUIC listeners do not bind port 0: the
/// first start picks free ports from <see cref="InteropPorts"/> (below the OS ephemeral range, so no client socket
/// takes one while the server is stopped) and retries with new ones while a port turns out to be taken. Restarts keep
/// the ports and the UDS path, and retry for a short while when a port is still taken. Kestrel binds port 0.
/// </remarks>
public sealed class InteropServer<TServerNexus, TClientProxy> : IAsyncDisposable, IInteropServerLifetime
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private const int MaxBindAttempts = 5;
    private const int MaxRestartBindAttempts = 20;
    private static readonly TimeSpan RestartBindRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly bool _ownsHost;
    private readonly string _udsDirectory;
    private readonly IReadOnlyDictionary<InteropEndpointKind, ServerConfig> _configs;
    private readonly IList<string> _allowedOrigins;
    private readonly ConcurrentQueue<string> _requestOrigins = new();
    private bool _portsAssigned;
    private StoppedPortHold? _stoppedPortHold;
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
    /// The <c>Origin</c> header of every HttpSocket and WebSocket request this server received with one, in arrival
    /// order, allowed or not. A browser always sends the origin of its page and other clients send none, so a test
    /// learns a browser client's page origin here.
    /// </summary>
    public IReadOnlyCollection<string> RequestOrigins => _requestOrigins.ToArray();

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
                // The folder is this server's own; a socket file left in it by a listener that did not clean up
                // (a crash) must not fail a restart.
                RemoveStaleSocketFile = true,
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
            app.Use((context, next) =>
            {
                var origin = context.Request.Headers.Origin;
                if (!StringValues.IsNullOrEmpty(origin))
                    _requestOrigins.Enqueue(origin.ToString());
                return next(context);
            });

            var httpSocketOptions = new HttpSocketOptions();
            httpSocketOptions.AllowedOrigins.AddRange(_allowedOrigins);
            app.UseHttpSockets(httpSocketOptions);
            app.MapNexusHttpSocket(Server, (HttpSocketServerConfig)_configs[httpSocketKind], httpSocketKind.GetEndpointName());
            app.MapNexusWebSocket(Server, (WebSocketServerConfig)_configs[webSocketKind], webSocketKind.GetEndpointName());
        });
    }

    /// <summary>
    /// Starts the server: the first time on newly picked TCP, TLS and QUIC ports, afterwards on the same ones, which
    /// <see cref="StopAsync"/> held until now.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ReleaseStoppedPorts();
        var tcp = (TcpServerConfig)_configs[InteropEndpointKind.Tcp];
        var tls = (TcpServerConfig)_configs[InteropEndpointKind.TcpTls];
        var quic = _configs.TryGetValue(InteropEndpointKind.Quic, out var quicConfig) ? (QuicServerConfig)quicConfig : null;

        for (var attempt = 1; ; attempt++)
        {
            if (!_portsAssigned)
            {
                tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, InteropPorts.Next());
                tls.EndPoint = new IPEndPoint(IPAddress.Loopback, InteropPorts.Next());
                if (quic != null)
                    quic.EndPoint = new IPEndPoint(IPAddress.Loopback, InteropPorts.Next());
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
            catch (TransportException e) when (e.Error == TransportError.AddressInUse
                                               && _portsAssigned
                                               && attempt < MaxRestartBindAttempts)
            {
                // A restart must keep its ports, as clients hold the endpoints. The ports are outside the ephemeral
                // range, so only another process probing or binding the same port can hold one: wait for it to let go.
                await Task.Delay(RestartBindRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!_portsAssigned)
        {
            _portsAssigned = true;

            Endpoints = new InteropEndpoints(
                (IPEndPoint)tcp.EndPoint,
                (IPEndPoint)tls.EndPoint,
                ((UdsServerConfig)_configs[InteropEndpointKind.Uds]).EndPoint.ToString(),
                AspUri(Host.HttpBaseUri, InteropEndpointKind.HttpSocket, "http"),
                AspUri(Host.HttpsBaseUri, InteropEndpointKind.HttpSocketTls, "https"),
                AspUri(Host.HttpBaseUri, InteropEndpointKind.WebSocket, "ws"),
                AspUri(Host.HttpsBaseUri, InteropEndpointKind.WebSocketTls, "wss"),
                (IPEndPoint?)quic?.EndPoint);
        }
    }

    /// <summary>
    /// Stops the server. It can be started again with <see cref="StartAsync"/> on the same endpoints.
    /// </summary>
    /// <remarks>
    /// Until it starts again (or is disposed), the server keeps its TCP, TLS and QUIC ports bound without listening, so
    /// a client's connection attempt is refused instead of reaching a server of another test process that picked a
    /// freed port (the port allocator keeps ports unique within a process only). The UDS path is the server's own and
    /// the HTTP endpoints stay on their running host, which answers for the stopped server with no NexNet session.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A port of the stopped server could not be held: another process has it.
    /// </exception>
    public async Task StopAsync()
    {
        await Server.StopAsync().ConfigureAwait(false);
        if (Endpoints == null || Volatile.Read(ref _disposed) != 0)
            return;

        ReleaseStoppedPorts();
        _stoppedPortHold = await StoppedPortHold.TakeAsync(Endpoints).ConfigureAwait(false);
    }
    private void ReleaseStoppedPorts() => Interlocked.Exchange(ref _stoppedPortHold, null)?.Dispose();

    private Uri AspUri(Uri baseUri, InteropEndpointKind kind, string scheme)
        => new UriBuilder(baseUri) { Scheme = scheme, Path = ((AspServerConfig)_configs[kind]).Path }.Uri;

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

        ReleaseStoppedPorts();
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
