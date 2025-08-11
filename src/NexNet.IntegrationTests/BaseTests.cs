using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.Uds;
using NexNet.Transports.WebSocket;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
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
        HttpSocket
    }

    private int _counter;
    private DirectoryInfo? _socketDirectory;
    private UnixDomainSocketEndPoint? CurrentPath;
    private int? _currentTcpPort;
    private int? _currentUdpPort;
    private List<INexusServer> Servers = new List<INexusServer>();
    private List<INexusClient> Clients = new List<INexusClient>();
    private RollingLogger _logger = null!;
    private BasePipeTests.LogMode _loggerMode;
    private readonly List<WebApplication> AspServers = new List<WebApplication>();
    public RollingLogger Logger => _logger;

    public int? CurrentTcpPort
    {
        get => _currentTcpPort;
        set => _currentTcpPort = value;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        _socketDirectory = Directory.CreateTempSubdirectory("socketTests");

    }

    [OneTimeTearDown]
    public virtual void OneTimeTearDown()
    {
        _loggerMode = BasePipeTests.LogMode.None;
        _socketDirectory?.Delete(true);
        Trace.Flush();
    }

    [SetUp]
    public virtual void SetUp()
    {
        _logger = new RollingLogger();
    }

    [TearDown]
    public virtual void TearDown()
    {
        if (_loggerMode == BasePipeTests.LogMode.OnTestFail)
        {
            if (TestContext.CurrentContext.Result.Outcome != ResultState.Success)
            {
                _logger.Flush(TestContext.Out);
            }
        }

        if (_loggerMode == BasePipeTests.LogMode.Always)
        {
            _logger.Flush(TestContext.Out);
        }

        CurrentPath = null;
        _currentTcpPort = null;
        _currentUdpPort = null;

        _logger.LogEnabled = false;

        foreach (var nexusClient in Clients)
        {
            if (nexusClient.State != ConnectionState.Connected)
                continue;

            _ = nexusClient.DisconnectAsync();
        }

        Clients.Clear();

        foreach (var nexusServer in Servers)
        {
            if (nexusServer.State != NexusServerState.Running)
                continue;

            _ = nexusServer.StopAsync();
        }

        Servers.Clear();
        foreach (var se in AspServers)
        {
            try
            {
                if (!se.Lifetime.ApplicationStarted.IsCancellationRequested)
                    continue;

                se.Lifetime.StopApplication();
            }
            catch (ObjectDisposedException)
            {
            }

        }

        AspServers.Clear();

    }

    protected ServerConfig CreateServerConfigWithLog(Type type, INexusLogger? logger = null)
    {
        if (type == Type.Uds)
        {
            CurrentPath ??=
                new UnixDomainSocketEndPoint(Path.Combine(_socketDirectory!.FullName,
                    $"socket_{Interlocked.Increment(ref _counter)}"));

            return new UdsServerConfig() { EndPoint = CurrentPath, Logger = logger };
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
                    ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certPass")
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
                    ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certPass")
                },
            };
        }



        if (type == Type.WebSocket)
        {
            _currentTcpPort ??= FreeTcpPort();
            
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            return new WebSocketServerConfig() { Path = "/websocket-test", Logger = logger };
        }

        if (type == Type.HttpSocket)
        {
            _currentTcpPort ??= FreeTcpPort();
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            return new HttpSocketServerConfig() { Path = "/httpsocket-test", Logger = logger, };
        }


        throw new InvalidOperationException();
    }

    protected ServerConfig CreateServerConfig(Type type, BasePipeTests.LogMode log = BasePipeTests.LogMode.OnTestFail)
    {
        _loggerMode = log;
        var logger = log != BasePipeTests.LogMode.None
            ? _logger.CreatePrefixedLogger(null, "SV")
            : null;
        return CreateServerConfigWithLog(type, logger);
    }

    protected ClientConfig CreateClientConfigWithLog(Type type, INexusLogger? logger = null)
    {
        if (type == Type.Uds)
        {
            CurrentPath ??=
                new UnixDomainSocketEndPoint(Path.Combine(_socketDirectory!.FullName,
                    $"socket_{Interlocked.Increment(ref _counter)}"));

            return new UdsClientConfig() { EndPoint = CurrentPath, Logger = logger, };
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
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
        }

        if (type == Type.WebSocket)
        {
            _currentTcpPort ??= FreeTcpPort();
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            
            return new WebSocketClientConfig()
            {
                Url = new Uri($"ws://127.0.0.1:{_currentTcpPort}/websocket-test"), 
                Logger = logger,
            };
        }

        if (type == Type.HttpSocket)
        {
            _currentTcpPort ??= FreeTcpPort();
            if(logger != null)
                logger.Behaviors |= NexusLogBehaviors.LogTransportData;
            
            return new HttpSocketClientConfig()
            {
                Url = new Uri($"http://127.0.0.1:{_currentTcpPort}/httpsocket-test"), 
                Logger = logger,
            };
        }

        throw new InvalidOperationException();
    }

    protected ClientConfig CreateClientConfig(Type type, BasePipeTests.LogMode log = BasePipeTests.LogMode.OnTestFail)
    {
        _loggerMode = log;
        
        var logger = log != BasePipeTests.LogMode.None
            ? _logger.CreatePrefixedLogger(null, "CL")
            : null;

        return CreateClientConfigWithLog(type, logger);
    }



    protected (NexusServer<ServerNexus, ServerNexus.ClientProxy> server,
        ServerNexus serverNexus,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus)
        CreateServerClient(ServerConfig sConfig, ClientConfig cConfig, bool startServer = true)
    {
        var serverNexus = new ServerNexus();
        var clientNexus = new ClientNexus();
        var server = ServerNexus.CreateServer(sConfig, () => serverNexus);
        var client = ClientNexus.CreateClient(cConfig, clientNexus);
        Servers.Add(server);
        Clients.Add(client);


        if (sConfig is WebSocketServerConfig || sConfig is HttpSocketServerConfig)
        {
            _currentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, _currentTcpPort.Value));
            
            builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();

            if (sConfig.Logger != null)
                builder.Logging.AddProvider(new AspLoggerProviderBridge(sConfig.Logger));

            var app = builder.Build();

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

        return (server, serverNexus, client, clientNexus);
    }

    protected (NexusServer<ServerNexus, ServerNexus.ClientProxy> server,
        ServerNexus serverNexus,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> client,
        ClientNexus clientNexus,
        Action startAspServer,
        Action stopAspServer)
        CreateServerClientWithStoppedServer(ServerConfig sConfig, ClientConfig cConfig)
    {
        var serverNexus = new ServerNexus();
        var clientNexus = new ClientNexus();
        var server = ServerNexus.CreateServer(sConfig, () => serverNexus);
        var client = ClientNexus.CreateClient(cConfig, clientNexus);
        Servers.Add(server);
        Clients.Add(client);

        WebApplication? app = null;

        if (sConfig is WebSocketServerConfig || sConfig is HttpSocketServerConfig)
        {
            _currentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, _currentTcpPort.Value));
            
            builder.Services.AddNexusServer<ServerNexus, ServerNexus.ClientProxy>();

            if (sConfig.Logger != null)
                builder.Logging.AddProvider(new AspLoggerProviderBridge(sConfig.Logger));

            app = builder.Build();

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


        return (server, serverNexus, client, clientNexus, StartAspServer, StopAspServer);
    }

    protected NexusServer<ServerNexus, ServerNexus.ClientProxy> CreateServer(
        ServerConfig sConfig,
        Action<ServerNexus>? nexusCreated,
        Action<WebApplicationBuilder>? OnAspCreateServices = null,
        Action<WebApplication>? OnAspAppConfigure = null)
    {
        
        return CreateServer<ServerNexus, ServerNexus.ClientProxy>(sConfig, nexusCreated, OnAspCreateServices, OnAspAppConfigure);
    }
    
    protected NexusServer<TServerNexus, TClientProxy> CreateServer<TServerNexus, TClientProxy>(
        ServerConfig sConfig,
        Action<TServerNexus>? nexusCreated,
        Action<WebApplicationBuilder>? OnAspCreateServices = null,
        Action<WebApplication>? OnAspAppConfigure = null) 
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer, new() 
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var server = new NexusServer<TServerNexus, TClientProxy>(sConfig, () =>
        {
            var nexus = new TServerNexus();
            nexusCreated?.Invoke(nexus);
            return nexus;
        });

        Servers.Add(server);

        if (sConfig is WebSocketServerConfig || sConfig is HttpSocketServerConfig)
        {
            _currentTcpPort ??= FreeTcpPort();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
                serverOptions.Listen(IPAddress.Loopback, _currentTcpPort.Value));

            builder.Services.AddNexusServer<TServerNexus, TClientProxy>();

            if (sConfig.Logger != null)
                builder.Logging.AddProvider(new AspLoggerProviderBridge(sConfig.Logger));
            
            OnAspCreateServices?.Invoke(builder);

            var app = builder.Build();
            
            OnAspAppConfigure?.Invoke(app);

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

    protected int FreeTcpPort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    protected int FreeUdpPort()
    {
        IPEndPoint localEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        using var udpServer = new UdpClient();
        udpServer.ExclusiveAddressUse = true;
        udpServer.Client.Bind(localEndPoint);
        return ((IPEndPoint)udpServer.Client.LocalEndPoint!).Port;
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

        Assert.That(thrown?.GetType(), Is.EqualTo(typeof(T)));
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
                _logger.Log((NexusLogLevel)logLevel, _logger.Category, exception,
                    formatter?.Invoke(state, exception) ?? "");
            }
        }
    }
}
