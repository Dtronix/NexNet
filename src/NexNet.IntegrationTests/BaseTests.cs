using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.InteropServer;
using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Pipes;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.Uds;
using NexNet.Transports.WebSocket;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NexNet.IntegrationTests;

internal abstract class BaseTests
{
    public enum Type
    {
        Uds,
        Tcp,
        TcpTls,
        Quic,
        WebSocket,
        HttpSocket,
        WebSocketTls,
        HttpSocketTls
    }

    private static int _counter;
    private DirectoryInfo? _testSocketDirectory;
    private UnixDomainSocketEndPoint? _currentUdsPath;
    private int? _currentTcpPort;
    private int? _currentUdpPort;
    private List<INexusServer> Servers = new ();
    private List<INexusServerFactory> ServerFactories = new ();
    private List<INexusClient> Clients = new();
    private RollingLogger _logger = null!;
    private BasePipeTests.LogMode _loggerMode;
    private readonly List<WebApplication> AspServers = new List<WebApplication>();
    private readonly List<StoppedPortHold> _portHolds = [];
    private readonly Dictionary<INexusServer, StoppedPortHold> _stoppedServerHolds = [];
    
    
    public RollingLogger Logger => _logger;

    /// <summary>
    /// Servers and ASP.NET apps this test created; the base TearDown stops them and waits for them.
    /// </summary>
    protected IReadOnlyList<INexusServer> TrackedServers => Servers;

    /// <inheritdoc cref="TrackedServers"/>
    protected IReadOnlyList<WebApplication> TrackedAspServers => AspServers;

    /// <summary>
    /// Applied to the Kestrel endpoint of ASP servers created by this test, e.g. to enable HTTPS.
    /// Cleared after each test.
    /// </summary>
    protected Action<ListenOptions>? AspListenOptions { get; set; }

    /// <summary>
    /// Applied to the Kestrel endpoint of every ASP server: HTTPS with the F08 test server certificate for the
    /// WebSocketTls/HttpSocketTls transports, then <see cref="AspListenOptions"/>.
    /// </summary>
    private void ConfigureAspListen(ListenOptions listen)
    {
        if (_aspHttps)
            listen.UseHttps(InteropCertificates.LoadServerCertificate());
        AspListenOptions?.Invoke(listen);
    }

    private bool _aspHttps;

    public int? CurrentTcpPort
    {
        get => _currentTcpPort;
        set => _currentTcpPort = value;
    }

    protected UnixDomainSocketEndPoint? CurrentUdsPath
    {
        get => _currentUdsPath;
        set => _currentUdsPath = value;
    }

    protected BasePipeTests.LogMode LoggerMode
    {
        get => _loggerMode;
        set => _loggerMode = value;
    }

    /// <summary>
    /// Directory for the UDS socket files of the run. Each test gets its own subdirectory (see
    /// <see cref="NewUdsEndPoint"/>), deleted after the test; the whole directory is deleted after the run.
    /// </summary>
    private static readonly Lazy<DirectoryInfo> SocketDirectory =
        new(() => Directory.CreateTempSubdirectory("socketTests"), LazyThreadSafetyMode.ExecutionAndPublication);

    [After(HookType.Assembly)]
    public static void DeleteSocketDirectory()
    {
        if (!SocketDirectory.IsValueCreated)
            return;

        try
        {
            SocketDirectory.Value.Delete(true);
        }
        catch (IOException)
        {
            // Best effort: a socket file can still be held open.
        }
    }

    /// <summary>
    /// Runs before each test: creates the test's logger, then calls <see cref="SetUp"/>.
    /// </summary>
    [Before(HookType.Test)]
    public void BaseSetUp()
    {
        _logger = new RollingLogger();
        SetUp();
    }

    /// <summary>
    /// Runs after each test: calls <see cref="TearDown"/>, which disconnects the clients and stops the servers this test
    /// created, then writes the log as <see cref="LoggerMode"/> asks.
    /// </summary>
    [After(HookType.Test)]
    public async Task BaseTearDown(TestContext context)
    {
        try
        {
            await TearDown();
        }
        finally
        {
            var failed = context.Execution.Result?.State is TestState.Failed or TestState.Timeout;
            if (LoggerMode == BasePipeTests.LogMode.Always
                || (LoggerMode == BasePipeTests.LogMode.OnTestFail && failed))
            {
                _logger.Flush(context.Output.StandardOutput);
            }
        }
    }

    /// <summary>
    /// Per-test setup for derived classes; runs after the base setup.
    /// </summary>
    protected virtual void SetUp()
    {
    }

    /// <summary>
    /// Per-test cleanup: disconnects the clients and stops the servers and ASP.NET apps this test created, and waits
    /// for them. Overrides must call the base.
    /// </summary>
    protected virtual async Task TearDown()
    {
        // The test is over: the ports held for its stopped servers and its no-server endpoints go back.
        ReleasePortHolds();

        CurrentUdsPath = null;
        AspListenOptions = null;
        _aspHttps = false;
        _currentTcpPort = null;
        _currentUdpPort = null;

        _logger.LogEnabled = false;

        var stops = new List<Task>();

        // The test is over, so nothing needs the disconnect message to reach the peer: skip the DisconnectDelay
        // (200 ms by default) that every graceful disconnect would otherwise wait out.
        foreach (var nexusClient in Clients)
            nexusClient.Config.DisconnectDelay = 0;

        foreach (var nexusServer in Servers.Concat(ServerFactories.Select(f => f.ServerBase)))
        {
            if (nexusServer.State == NexusServerState.Running)
                nexusServer.Config.DisconnectDelay = 0;
        }

        foreach (var nexusClient in Clients)
        {
            if (nexusClient.State != ConnectionState.Connected)
                continue;

            stops.Add(nexusClient.DisconnectAsync());
        }

        Clients.Clear();

        foreach (var nexusServer in Servers)
        {
            if (nexusServer.State != NexusServerState.Running)
                continue;

            stops.Add(nexusServer.StopAsync());
        }

        Servers.Clear();

        foreach (var nexusServer in ServerFactories)
        {
            if (nexusServer.ServerBase.State != NexusServerState.Running)
                continue;

            stops.Add(nexusServer.ServerBase.StopAsync());
        }

        ServerFactories.Clear();

        foreach (var se in AspServers)
        {
            try
            {
                if (!se.Lifetime.ApplicationStarted.IsCancellationRequested)
                    continue;

                stops.Add(se.StopAsync());
            }
            catch (ObjectDisposedException)
            {
            }
        }

        AspServers.Clear();

        // Wait for the stops, bounded so that a hung stop cannot hang the run; a failing stop does not fail the test.
        try
        {
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Ignore: the test's own result is what matters here.
        }

        DeleteTestSocketDirectory();
    }

    private void ReleasePortHolds()
    {
        foreach (var factory in ServerFactories)
            factory.ReleaseStoppedPorts();

        lock (_portHolds)
        {
            foreach (var hold in _portHolds.Concat(_stoppedServerHolds.Values))
                hold.Dispose();
            _portHolds.Clear();
            _stoppedServerHolds.Clear();
        }
    }

    /// <summary>
    /// Stops <paramref name="server"/> and holds its TCP or QUIC port (<see cref="StoppedPortHold"/>) until
    /// <see cref="StartServerAsync"/> starts it again or the test ends, so no server of another test process can listen
    /// on the port meanwhile (<see cref="TestPorts"/> keeps ports unique within a process only) and a client's attempt
    /// is refused. Use it, with <see cref="StartServerAsync"/>, for a TCP, TLS or QUIC server a test stops and restarts
    /// or keeps stopped; the server factories of <see cref="CreateServerClient(ServerConfig, ClientConfig, bool)"/>
    /// do the same in their own <c>StopAsync</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The port could not be held: another process has it.</exception>
    protected async Task StopServerAsync(INexusServer server)
    {
        await server.StopAsync();
        ReleaseStoppedServerPorts(server);
        var hold = await HoldStoppedServerPortsAsync(server);
        if (hold == null)
            return;

        lock (_portHolds)
            _stoppedServerHolds[server] = hold;
    }

    /// <summary>
    /// Starts <paramref name="server"/> on the port <see cref="StopServerAsync"/> held until now.
    /// </summary>
    protected Task StartServerAsync(INexusServer server, CancellationToken cancellationToken = default)
    {
        ReleaseStoppedServerPorts(server);
        return server.StartAsync(cancellationToken);
    }

    private void ReleaseStoppedServerPorts(INexusServer server)
    {
        lock (_portHolds)
        {
            if (_stoppedServerHolds.Remove(server, out var hold))
                hold.Dispose();
        }
    }

    /// <summary>
    /// Holds the TCP or QUIC port of <paramref name="server"/>'s primary endpoint, which has stopped; null for an
    /// endpoint without one (UDS, whose socket path is the test's own, or ASP.NET, whose app keeps its port).
    /// </summary>
    /// <exception cref="InvalidOperationException">The port could not be held: another process has it.</exception>
    internal static async Task<StoppedPortHold?> HoldStoppedServerPortsAsync(INexusServer server) => server.Config switch
    {
        TcpServerConfig { EndPoint: IPEndPoint tcp } => await StoppedPortHold.TakeAsync([tcp], []),
        QuicServerConfig quic => await StoppedPortHold.TakeAsync([], [quic.EndPoint]),
        _ => null,
    };

    /// <summary>
    /// Endpoints with no server: free TCP, TLS, HTTP, HTTPS and QUIC ports, held (<see cref="StoppedPortHold"/>) until
    /// the test ends so no server of another test process can take one meanwhile, and a UDS path with no socket file.
    /// A connection attempt is refused: the TCP ports are bound without listening, and the QUIC port's UDP port is left
    /// unbound (port unreachable) while its TCP port of the same number keeps the allocators off it.
    /// </summary>
    protected async Task<InteropEndpoints> NoServerEndpointsAsync()
    {
        static IPEndPoint FreeEndPoint() => new(IPAddress.Loopback, TestPorts.Next());
        var tcp = FreeEndPoint();
        var tls = FreeEndPoint();
        var http = FreeEndPoint();
        var https = FreeEndPoint();
        var quic = FreeEndPoint();

        var hold = await StoppedPortHold.TakeAsync([tcp, tls, http, https], [], refusedQuicEndPoints: [quic]);
        lock (_portHolds)
            _portHolds.Add(hold);

        return new InteropEndpoints(
            tcp,
            tls,
            NewUdsEndPoint().ToString(),
            new Uri($"http://{http}/httpsocket"),
            new Uri($"https://{https}/httpsocket"),
            new Uri($"ws://{http}/websocket"),
            new Uri($"wss://{https}/websocket"),
            quic);
    }

    /// <summary>
    /// A new UDS endpoint in this test's own socket directory, which is created on first use and deleted after the
    /// test, so tests running in parallel never share a socket file.
    /// </summary>
    protected UnixDomainSocketEndPoint NewUdsEndPoint()
    {
        _testSocketDirectory ??= SocketDirectory.Value.CreateSubdirectory(
            $"t{Interlocked.Increment(ref _counter)}");

        // Short names: a socket path is limited to about 100 characters.
        return new UnixDomainSocketEndPoint(Path.Combine(_testSocketDirectory.FullName,
            $"s{Interlocked.Increment(ref _counter)}"));
    }

    private void DeleteTestSocketDirectory()
    {
        var directory = _testSocketDirectory;
        _testSocketDirectory = null;
        if (directory == null)
            return;

        try
        {
            directory.Delete(true);
        }
        catch (IOException)
        {
            // Best effort: a socket file can still be held open; the run's directory is deleted at the end.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    protected ServerConfig CreateServerConfigWithLog(Type type, INexusLogger? logger = null)
    {
        if (type == Type.Uds)
        {
            CurrentUdsPath ??= NewUdsEndPoint();

            return new UdsServerConfig() { EndPoint = CurrentUdsPath, Logger = logger };
        }

        if (type == Type.Tcp)
        {
            _currentTcpPort ??= FreeTcpPort();

            return new TcpServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _currentTcpPort.Value),
                Logger = logger,
                TcpNoDelay = true
            };
        }

        if (type == Type.TcpTls)
        {
            _currentTcpPort ??= FreeTcpPort();

            return new TcpTlsServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _currentTcpPort.Value),
                Logger = logger,
                TcpNoDelay = true,
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "server.pfx"), "certPass")
                },
            };
        }

        if (type == Type.Quic)
        {
            _currentUdpPort ??= FreeUdpPort();

            return new QuicServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _currentUdpPort.Value),
                Logger = logger,
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "server.pfx"), "certPass")
                },
            };
        }



        if (type is Type.WebSocket or Type.WebSocketTls)
        {
            _currentTcpPort ??= FreeTcpPort();
            _aspHttps |= type == Type.WebSocketTls;
            
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            return new WebSocketServerConfig() { Path = "/websocket-test", Logger = logger };
        }

        if (type is Type.HttpSocket or Type.HttpSocketTls)
        {
            _currentTcpPort ??= FreeTcpPort();
            _aspHttps |= type == Type.HttpSocketTls;
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            return new HttpSocketServerConfig() { Path = "/httpsocket-test", Logger = logger, };
        }


        throw new InvalidOperationException();
    }

    protected ServerConfig CreateServerConfig(Type type, BasePipeTests.LogMode log = BasePipeTests.LogMode.OnTestFail)
    {
        LoggerMode = log;
        var logger = log != BasePipeTests.LogMode.None
            ? _logger
            : null;
        return CreateServerConfigWithLog(type, logger);
    }

    protected ClientConfig CreateClientConfigWithLog(Type type, INexusLogger? logger = null)
    {
        if (type == Type.Uds)
        {
            CurrentUdsPath ??= NewUdsEndPoint();

            return new UdsClientConfig() { EndPoint = CurrentUdsPath, Logger = logger, };
        }

        if (type == Type.Tcp)
        {
            _currentTcpPort ??= FreeTcpPort();

            return new TcpClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _currentTcpPort.Value), Logger = logger,
            };
        }

        if (type == Type.TcpTls)
        {
            _currentTcpPort ??= FreeTcpPort();

            return new TcpTlsClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _currentTcpPort.Value),
                Logger = logger,
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
                {
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
        }

        if (type == Type.Quic)
        {
            _currentUdpPort ??= FreeUdpPort();

            return new QuicClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _currentUdpPort.Value),
                Logger = logger,
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
                {
                    // msquic 2.5.11+/2.6.1+ fails the TLS handshake without a DNS server name (SNI).
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
        }

        if (type is Type.WebSocket or Type.WebSocketTls)
        {
            _currentTcpPort ??= FreeTcpPort();
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            
            return new WebSocketClientConfig()
            {
                Url = new Uri($"{(type == Type.WebSocketTls ? "wss" : "ws")}://127.0.0.1:{_currentTcpPort}/websocket-test"), 
                Logger = logger,
                SslClientAuthenticationOptions = type == Type.WebSocketTls ? TestCaTlsOptions() : null,
            };
        }

        if (type is Type.HttpSocket or Type.HttpSocketTls)
        {
            _currentTcpPort ??= FreeTcpPort();
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            
            return new HttpSocketClientConfig()
            {
                Url = new Uri($"{(type == Type.HttpSocketTls ? "https" : "http")}://127.0.0.1:{_currentTcpPort}/httpsocket-test"), 
                Logger = logger,
                SslClientAuthenticationOptions = type == Type.HttpSocketTls ? TestCaTlsOptions() : null,
            };
        }

        throw new InvalidOperationException();
    }

    /// <summary>
    /// TLS options of the WebSocketTls/HttpSocketTls clients: trust only the F08 test CA, with host name checks.
    /// </summary>
    private static SslClientAuthenticationOptions TestCaTlsOptions() => new()
    {
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        RemoteCertificateValidationCallback = InteropCertificates.CreateValidationCallback(InteropCertificates.LoadTestCa()),
    };

    protected ClientConfig CreateClientConfig(Type type, BasePipeTests.LogMode log = BasePipeTests.LogMode.OnTestFail)
    {
        LoggerMode = log;
        
        var logger = log != BasePipeTests.LogMode.None
            ? _logger
            : null;

        return CreateClientConfigWithLog(type, logger);
    }



    protected (
        NexusServerFactory<ServerNexus, ServerNexus.ClientProxy> serverFactory, 
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client, 
        ClientNexus clientNexus)
        CreateServerClient(ServerConfig sConfig, ClientConfig cConfig, bool startServer = true)
    {
        var serverFactory = new NexusServerFactory<ServerNexus, ServerNexus.ClientProxy>(sConfig);
        var clientNexus = new ClientNexus();
        var client = ClientNexus.CreateClient(cConfig, clientNexus);
        ServerFactories.Add(serverFactory);
        Clients.Add(client);


        if (sConfig is WebSocketServerConfig || sConfig is HttpSocketServerConfig)
        {
            _currentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, _currentTcpPort.Value, ConfigureAspListen));
            
            builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();

            if (sConfig.Logger != null)
                builder.Logging.AddProvider(new AspLoggerProviderBridge(sConfig.Logger));

            var app = builder.Build();

            if (sConfig is WebSocketServerConfig sWebSocketConfig)
            {
                app.UseWebSockets();
                app.MapWebSocketNexus(sWebSocketConfig, serverFactory.Server);
            }
            else if (sConfig is HttpSocketServerConfig sHttpSocketConfig)
            {
                app.UseHttpSockets();
                app.MapHttpSocketNexus(sHttpSocketConfig, serverFactory.Server);
            }
            
            

            _ = app.RunAsync();
            AspServers.Add(app);
        }

        return (serverFactory, client, clientNexus);
    }

    protected (NexusServerFactory<ServerNexus, ServerNexus.ClientProxy> serverFactory,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus,
        Action startAspServer,
        Action stopAspServer)
        CreateServerClientWithStoppedServer(ServerConfig sConfig, ClientConfig cConfig)
    {
        var serverFactory = new NexusServerFactory<ServerNexus, ServerNexus.ClientProxy>(sConfig);
        var clientNexus = new ClientNexus();
        var client = ClientNexus.CreateClient(cConfig, clientNexus);
        ServerFactories.Add(serverFactory);
        Clients.Add(client);

        WebApplication? app = null;

        if (sConfig is WebSocketServerConfig || sConfig is HttpSocketServerConfig)
        {
            _currentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, _currentTcpPort.Value, ConfigureAspListen));
            
            builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();

            if (sConfig.Logger != null)
                builder.Logging.AddProvider(new AspLoggerProviderBridge(sConfig.Logger));

            app = builder.Build();

            if (sConfig is WebSocketServerConfig sWebSocketConfig)
            {
                app.UseWebSockets();
                app.MapWebSocketNexus(sWebSocketConfig, serverFactory.Server);
            }
            else if (sConfig is HttpSocketServerConfig sHttpSocketConfig)
            {
                app.UseHttpSockets();
                app.MapHttpSocketNexus(sHttpSocketConfig, serverFactory.Server);

            }

            AspServers.Add(app);
        }

        void StartAspServer()
        {
            if (app == null)
                return;
            _ = app.RunAsync();
        }

        void StopAspServer()
        {
            if (app == null)
                return;

            if (!app.Lifetime.ApplicationStarted.IsCancellationRequested)
                return;

            app.Lifetime.StopApplication();

            app.Lifetime.ApplicationStopped.WaitHandle.WaitOne(500);
        }


        return (serverFactory, client, clientNexus, StartAspServer, StopAspServer);
    }

    protected NexusServer<ServerNexus, ServerNexus.ClientProxy> CreateServer(
        ServerConfig sConfig,
        Action<ServerNexus>? nexusCreated,
        Action<WebApplicationBuilder>? onAspCreateServices = null,
        Action<WebApplication>? onAspAppConfigure = null,
        Action<ServerNexus>? configureCollections = null)
    {
        
        return CreateServer<ServerNexus, ServerNexus.ClientProxy>(sConfig, nexusCreated, onAspCreateServices, onAspAppConfigure, configureCollections);
    }
    
    protected NexusServer<TServerNexus, TClientProxy> CreateServer<TServerNexus, TClientProxy>(
        ServerConfig sConfig,
        Action<TServerNexus>? nexusCreated,
        Action<WebApplicationBuilder>? onAspCreateServices = null,
        Action<WebApplication>? onAspAppConfigure = null,
        Action<TServerNexus>? configureCollections = null
        ) 
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new() 
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var server = new NexusServer<TServerNexus, TClientProxy>(sConfig, () =>
        {
            var nexus = new TServerNexus();
            nexusCreated?.Invoke(nexus);
            return nexus;
        }, configureCollections);

        Servers.Add(server);

        if (sConfig is WebSocketServerConfig || sConfig is HttpSocketServerConfig)
        {
            _currentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, _currentTcpPort.Value, ConfigureAspListen));

            builder.Services.AddNexusServer<TServerNexus, TClientProxy>();

            if (sConfig.Logger != null)
                builder.Logging.AddProvider(new AspLoggerProviderBridge(sConfig.Logger));
            
            onAspCreateServices?.Invoke(builder);

            var app = builder.Build();
            
            onAspAppConfigure?.Invoke(app);

            if (sConfig is WebSocketServerConfig sWebSocketConfig)
            {
                app.UseWebSockets();
                app.MapWebSocketNexus(sWebSocketConfig, server);
            }
            else if (sConfig is HttpSocketServerConfig sHttpSocketConfig)
            {
                app.UseHttpSockets();
                app.MapHttpSocketNexus(sHttpSocketConfig, server);
            }
            


            _ = app.RunAsync();
            AspServers.Add(app);
        }

        return server;
    }

    /// <summary>
    /// A server with one endpoint per transport in <see cref="Transports"/>, built by
    /// <see cref="CreateMultiEndpointServer(Type[])"/>.
    /// </summary>
    /// <param name="Server">The server; not started.</param>
    /// <param name="Configs">Server configuration of each transport's endpoint.</param>
    /// <param name="Transports">The transports, the primary one first.</param>
    /// <param name="CreateClientConfig">Creates a client configuration that connects to the endpoint of a transport.</param>
    protected sealed record MultiEndpointServer(
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Server,
        IReadOnlyDictionary<Type, ServerConfig> Configs,
        IReadOnlyList<Type> Transports,
        Func<Type, ClientConfig> CreateClientConfig);

    /// <summary>
    /// Creates a stopped server with one endpoint per transport in <paramref name="types"/>: the first is the primary
    /// configuration, the others are added with <c>AddListener</c> or, for WebSocket and HttpSocket, mapped on one
    /// ASP.NET app with <c>MapNexusWebSocket</c> / <c>MapNexusHttpSocket</c>. Each TCP-based endpoint gets its own
    /// port, and each endpoint is named after its transport.
    /// </summary>
    protected MultiEndpointServer CreateMultiEndpointServer(params Type[] types)
        => CreateMultiEndpointServer(null, null, types);

    /// <inheritdoc cref="CreateMultiEndpointServer(Type[])"/>
    /// <param name="nexusCreated">Called with every nexus the server creates.</param>
    /// <param name="configure">Called with each endpoint's configuration before it is added to the server.</param>
    /// <param name="types">The transports, the primary one first.</param>
    protected MultiEndpointServer CreateMultiEndpointServer(
        Action<ServerNexus>? nexusCreated,
        Action<Type, ServerConfig>? configure,
        params Type[] types)
        => CreateMultiEndpointServer(nexusCreated, configure, null, null, types);

    /// <inheritdoc cref="CreateMultiEndpointServer(Type[])"/>
    /// <param name="nexusCreated">Called with every nexus the server creates.</param>
    /// <param name="configure">Called with each endpoint's configuration before it is added to the server.</param>
    /// <param name="onAspCreateServices">Adds services to the ASP.NET app, when there is one.</param>
    /// <param name="onAspAppConfigure">Adds middleware to the ASP.NET app before the NexNet endpoints.</param>
    /// <param name="types">The transports, the primary one first.</param>
    protected MultiEndpointServer CreateMultiEndpointServer(
        Action<ServerNexus>? nexusCreated,
        Action<Type, ServerConfig>? configure,
        Action<WebApplicationBuilder>? onAspCreateServices,
        Action<WebApplication>? onAspAppConfigure,
        params Type[] types)
    {
        if (types.Length == 0 || types.Distinct().Count() != types.Length)
            throw new ArgumentException("Give each transport once.", nameof(types));
        if (types.Any(t => t is Type.WebSocketTls or Type.HttpSocketTls))
            throw new NotSupportedException("The ASP.NET endpoints of a multi-endpoint server share one plain HTTP port.");

        LoggerMode = BasePipeTests.LogMode.OnTestFail;
        var savedTcpPort = _currentTcpPort;
        var savedUdsPath = CurrentUdsPath;
        var savedUdpPort = _currentUdpPort;

        // Where each transport's endpoint listens: its own TCP or UDP port or UDS path; the ASP.NET ones share one port.
        int? aspPort = null;
        var tcpPorts = new Dictionary<Type, int?>();
        var udsPaths = new Dictionary<Type, UnixDomainSocketEndPoint?>();
        var udpPorts = new Dictionary<Type, int?>();

        void Select(Type type)
        {
            _currentTcpPort = tcpPorts.GetValueOrDefault(type);
            CurrentUdsPath = udsPaths.GetValueOrDefault(type);
            _currentUdpPort = udpPorts.GetValueOrDefault(type);
            if (type is Type.WebSocket or Type.HttpSocket)
                _currentTcpPort = aspPort ??= FreeTcpPort();
        }

        void Remember(Type type)
        {
            tcpPorts[type] = _currentTcpPort;
            udsPaths[type] = CurrentUdsPath;
            udpPorts[type] = _currentUdpPort;
        }

        var configs = new Dictionary<Type, ServerConfig>();
        foreach (var type in types)
        {
            Select(type);
            var config = CreateServerConfigWithLog(type, _logger);
            Remember(type);
            configure?.Invoke(type, config);
            configs[type] = config;
        }

        _currentTcpPort = savedTcpPort;
        CurrentUdsPath = savedUdsPath;
        _currentUdpPort = savedUdpPort;

        var primary = configs[types[0]];
        var server = new NexusServer<ServerNexus, ServerNexus.ClientProxy>(primary, () =>
        {
            var nexus = new ServerNexus();
            nexusCreated?.Invoke(nexus);
            return nexus;
        });
        Servers.Add(server);

        foreach (var type in types.Skip(1))
        {
            if (type is not (Type.WebSocket or Type.HttpSocket))
                server.AddListener(configs[type], configs[type].TransportName);
        }

        if (aspPort != null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((_, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, aspPort.Value, ConfigureAspListen));
            builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();
            builder.Logging.AddProvider(new AspLoggerProviderBridge(_logger));
            onAspCreateServices?.Invoke(builder);

            var app = builder.Build();
            onAspAppConfigure?.Invoke(app);
            app.UseWebSockets();
            app.UseHttpSockets();

            foreach (var type in types)
            {
                // The primary configuration maps to the primary endpoint; the others are added as endpoints.
                if (configs[type] is WebSocketServerConfig webSocketConfig)
                    app.MapNexusWebSocket(server, webSocketConfig);
                else if (configs[type] is HttpSocketServerConfig httpSocketConfig)
                    app.MapNexusHttpSocket(server, httpSocketConfig);
            }

            _ = app.RunAsync();
            AspServers.Add(app);
        }

        ClientConfig CreateEndpointClientConfig(Type type)
        {
            if (!configs.ContainsKey(type))
                throw new ArgumentException($"The server has no {type} endpoint.", nameof(type));

            var tcpPort = _currentTcpPort;
            var udsPath = CurrentUdsPath;
            var udpPort = _currentUdpPort;
            try
            {
                Select(type);
                return CreateClientConfigWithLog(type, _logger);
            }
            finally
            {
                _currentTcpPort = tcpPort;
                CurrentUdsPath = udsPath;
                _currentUdpPort = udpPort;
            }
        }

        return new MultiEndpointServer(server, configs, types, CreateEndpointClientConfig);
    }

    protected (NexusClient<ClientNexus, ClientNexus.ServerProxy> client, ClientNexus clientNexus)
        CreateClient(ClientConfig cConfig)
    {
        return CreateClient<ClientNexus, ClientNexus.ServerProxy>(cConfig);
    }
    
    protected (NexusClient<TClientNexus, TProxy> client, TClientNexus clientNexus)
        CreateClient<TClientNexus, TProxy>(ClientConfig cConfig) 
        where TClientNexus : ClientNexusBase<TProxy>, IInvocationMethodHash, ICollectionConfigurer, new() 
        where TProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var clientNexus = new TClientNexus();
        var client = new NexusClient<TClientNexus, TProxy>(cConfig, clientNexus);
        Clients.Add(client);

        return (client, clientNexus);
    }

    /// <summary>
    /// A loopback TCP port no other test in this run has been given; see <see cref="TestPorts"/>.
    /// </summary>
    protected static int FreeTcpPort() => TestPorts.Next();

    /// <summary>
    /// A loopback UDP port no other test in this run has been given; see <see cref="TestPorts"/>.
    /// </summary>
    protected static int FreeUdpPort() => TestPorts.Next();

    /// <summary>
    /// Multiplier for every <see cref="T"/> timeout, from the NEXNET_TIMEOUT_SCALE environment variable (default 1).
    /// Raise it on slow machines and for clients that run out of process.
    /// </summary>
    protected static readonly double TimeoutScale = ReadTimeoutScale();

    private static double ReadTimeoutScale()
    {
        var value = Environment.GetEnvironmentVariable("NEXNET_TIMEOUT_SCALE");
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale > 0
            ? scale
            : 1;
    }

    /// <summary>
    /// A hang guard of <paramref name="seconds"/>, scaled by <see cref="TimeoutScale"/>, plus 250 ms of slack, for
    /// <c>.Timeout(T(x))</c>. Use it for every wait on a step whose duration depends on the machine; the
    /// <see cref="TestTimeouts"/> constants go through it too (<c>T(TestTimeouts.Connect)</c>).
    /// </summary>
    protected static double T(double seconds) => seconds * TimeoutScale + 0.25;

    /// <summary>
    /// Waits until <paramref name="client"/> reaches <paramref name="state"/>, for at most <c>T(seconds)</c>, then
    /// asserts it. Use it instead of reading the state right after an action: a client in another process reports
    /// its state asynchronously.
    /// </summary>
    protected static Task WaitForState(INexusClient client, ConnectionState state, string? because = null, double seconds = 1)
        => WaitForState(() => client.State, state, because, seconds);

    /// <inheritdoc cref="WaitForState(INexusClient, ConnectionState, string?, double)"/>
    protected static Task WaitForState<TProxy>(IRentedNexusClient<TProxy> client, ConnectionState state, string? because = null, double seconds = 1)
        where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash
        => WaitForState(() => client.State, state, because, seconds);

    private static async Task WaitForState(Func<ConnectionState> getState, ConnectionState state, string? because, double seconds)
    {
        var deadline = Environment.TickCount64 + (long)(T(seconds) * 1000);
        while (getState() != state && Environment.TickCount64 < deadline)
            await Task.Delay(5);

        if (because == null)
            await Assert.That(getState()).IsEqualTo(state);
        else
            await Assert.That(getState()).IsEqualTo(state).Because(because);
    }

    /// <summary>
    /// The client's writer on a pipe used as a channel of <typeparamref name="TItem"/>. Tests go through this helper
    /// instead of the channel extensions, so the cross-language harness can map it to its remote pipe operations.
    /// </summary>
    protected static ValueTask<INexusChannelWriter<TItem>> ChannelWriter<TItem>(INexusDuplexPipe pipe)
        => pipe.GetChannelWriter<TItem>();

    /// <summary>
    /// The client's reader on a pipe used as a channel of <typeparamref name="TItem"/>; see
    /// <see cref="ChannelWriter{TItem}"/>.
    /// </summary>
    protected static ValueTask<INexusChannelReader<TItem>> ChannelReader<TItem>(INexusDuplexPipe pipe)
        => pipe.GetChannelReader<TItem>();

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, for at most <c>T(seconds)</c>, and returns either way: the
    /// assertions after it report what is wrong. For state that changes without an event to wait for.
    /// </summary>
    protected static async Task WaitUntil(Func<bool> condition, double seconds = 1)
    {
        var deadline = Environment.TickCount64 + (long)(T(seconds) * 1000);
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(5);
    }

    /// <summary>
    /// A round trip from the client to the server. Frames on a session arrive in order, so once it returns the server
    /// has received everything the client sent before it. Use it before asserting that something did not arrive.
    /// The server nexus needs no handler: a missing one fails the call remotely, which still completes the round trip.
    /// </summary>
    protected static async Task Barrier(ClientNexus.ServerProxy proxy)
    {
        try
        {
            await proxy.ServerTaskValue().Timeout(T(1));
        }
        catch (ProxyRemoteInvocationException)
        {
            // The server answered.
        }
    }

    /// <summary>
    /// A round trip from the server to a client; see <see cref="Barrier(ClientNexus.ServerProxy)"/>.
    /// </summary>
    protected static async Task Barrier(ServerNexus.ClientProxy proxy)
    {
        try
        {
            await proxy.ClientTaskValue().Timeout(T(1));
        }
        catch (ProxyRemoteInvocationException)
        {
            // The client answered.
        }
    }

    public static async Task AssertThrows<T>(Func<Task> task)
        where T : Exception
    {
        Exception? thrown = null;
        try
        {
            await task.Invoke();
        }
        catch (Exception e)
        {
            thrown = e;
        }

        await Assert.That(thrown?.GetType()).IsEqualTo(typeof(T));
    }

    private class AspLoggerProviderBridge : ILoggerProvider
    {
        private readonly INexusLogger _logger;


        public AspLoggerProviderBridge(INexusLogger logger)
        {
            _logger = logger;
        }

        public void Dispose()
        {
            // Ignore
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new SubLogger(_logger.CreateLogger(categoryName));
        }

        class SubLogger : ILogger
        {
            private readonly INexusLogger _logger;

            public SubLogger(INexusLogger logger)
            {
                _logger = logger;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _logger.Log((NexusLogLevel)logLevel, null, exception,
                    formatter?.Invoke(state, exception) ?? "");
            }
        }
    }
}