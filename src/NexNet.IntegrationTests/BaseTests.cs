using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.TestHost;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Logging;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.Asp;
using NexNet.Transports.Asp.WebSocket;
using NexNet.Transports.WebSocket;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace NexNet.IntegrationTests;

internal class BaseTests
{
    public enum Type
    {
        Uds,
        Tcp,
        TcpTls,
        Quic,
        WebSocket
    }

    private int _counter;
    private DirectoryInfo? _socketDirectory;
    private UnixDomainSocketEndPoint? CurrentPath;
    private int? CurrentTcpPort;
    private int? CurrentUdpPort;
    private List<INexusServer> Servers = new List<INexusServer>();
    private List<INexusClient> Clients = new List<INexusClient>();
    private RollingLogger _logger = null!;
    private BasePipeTests.LogMode _loggerMode;
    private readonly List<WebApplication> AspServers = new List<WebApplication>();
    public bool BlockForClose { get; set; }
    public RollingLogger Logger => _logger;

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
        BlockForClose = false;
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
        CurrentTcpPort = null;
        CurrentUdpPort = null;

        _logger.LogEnabled = BlockForClose;
        
        foreach (var nexusClient in Clients)
        {
            if(nexusClient.State != ConnectionState.Connected)
                continue;

            _ = nexusClient.DisconnectAsync();

            if(BlockForClose)
                nexusClient.DisconnectedTask?.Wait();
        }
        Clients.Clear();

        foreach (var nexusServer in Servers)
        {
            if(!nexusServer.IsStarted)
                continue;

            _ = nexusServer.StopAsync();

            if (BlockForClose)
                nexusServer.StoppedTask?.Wait();
        }
        Servers.Clear();
        foreach (var se in AspServers)
        {
            if(!se.Lifetime.ApplicationStarted.IsCancellationRequested)
                continue;
            
            se.Lifetime.StopApplication();

            if (BlockForClose)
                se.Lifetime.ApplicationStopped.WaitHandle.WaitOne(5000);
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

            return new UdsServerConfig()
            {
                EndPoint = CurrentPath,
                Logger = logger
            };
        }

        if (type == Type.Tcp)
        {
            CurrentTcpPort ??= FreeTcpPort();

            return new TcpServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentTcpPort.Value),
                Logger = logger,
                TcpNoDelay = true
            };
        }

        if (type == Type.TcpTls)
        {
            CurrentTcpPort ??= FreeTcpPort();

            return new TcpTlsServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentTcpPort.Value),
                Logger = logger,
                TcpNoDelay = true,
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = new X509Certificate2("server.pfx", "certPass"),
                },
            };
        }

        if (type == Type.Quic)
        {
            CurrentUdpPort ??= FreeUdpPort();

            return new QuicServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentUdpPort.Value),
                Logger = logger,
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = new X509Certificate2("server.pfx", "certPass"),
                },
            };
        }
        
        if (type == Type.WebSocket)
        {
            return new WebSocketServerConfig()
            {
                
                Path = "/websocket-test",
                Logger = logger,
            };
        }


        throw new InvalidOperationException();
    }

    protected ServerConfig CreateServerConfig(Type type, BasePipeTests.LogMode log = BasePipeTests.LogMode.None)
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

            return new UdsClientConfig()
            {
                EndPoint = CurrentPath,
                Logger = logger,
            };
        }

        if (type == Type.Tcp)
        {
            CurrentTcpPort ??= FreeTcpPort();

            return new TcpClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentTcpPort.Value),
                Logger = logger,
            };
        }
        
        if (type == Type.TcpTls)
        {
            CurrentTcpPort ??= FreeTcpPort();

            return new TcpTlsClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentTcpPort.Value),
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
            CurrentUdpPort ??= FreeUdpPort();

            return new QuicClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentUdpPort.Value),
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
            return new WebSocketClientConfig()
            {
                Url = new Uri("ws://127.0.0.1:15050/websocket-test"),
                Logger = logger,
            };
        }

        throw new InvalidOperationException();
    }

    protected ClientConfig CreateClientConfig(Type type, BasePipeTests.LogMode log = BasePipeTests.LogMode.None)
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
        
        if (sConfig is WebSocketServerConfig sWebSocketConfig)
        {
            
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel((context, serverOptions) => serverOptions.Listen(IPAddress.Loopback, 15050));
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.UseAuthorization();
            app.UseNexNetWebSockets(server, sWebSocketConfig);
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
        if (sConfig is WebSocketServerConfig sWebSocketConfig)
        {
            
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel((context, serverOptions) => serverOptions.Listen(IPAddress.Loopback, 15050));
            builder.Services.AddAuthorization();
            app = builder.Build();
            app.UseAuthorization();
            app.UseNexNetWebSockets(server, sWebSocketConfig);
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
            
            if(!app.Lifetime.ApplicationStarted.IsCancellationRequested)
                return;
            
            app.Lifetime.StopApplication();

            app.Lifetime.ApplicationStopped.WaitHandle.WaitOne(500);
        }
        

        return (server, serverNexus, client, clientNexus, StartAspServer, StopAspServer);
    }

    protected NexusServer<ServerNexus, ServerNexus.ClientProxy>
        CreateServer(ServerConfig sConfig, Action<ServerNexus>? nexusCreated)
    {
        var server = ServerNexus.CreateServer(sConfig, () =>
        {
            var nexus = new ServerNexus();
            nexusCreated?.Invoke(nexus);
            return nexus;
        }); 
        
        Servers.Add(server);
        
        if (sConfig is WebSocketServerConfig sWebSocketConfig)
        {
            
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel((context, serverOptions) => serverOptions.Listen(IPAddress.Loopback, 15050));
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.UseAuthorization();
            NexNetMiddlewareExtensions.UseNexNetWebSockets(app, server, sWebSocketConfig);
            _ = app.RunAsync();
            AspServers.Add(app);
        }

        return server;
    }

    protected (NexusClient<ClientNexus, ClientNexus.ServerProxy> client, ClientNexus clientNexus)
        CreateClient(ClientConfig cConfig)
    {
        var clientNexus = new ClientNexus();
        var client = ClientNexus.CreateClient(cConfig, clientNexus);
        Clients.Add(client);

        return (client, clientNexus);
    }

    private int FreeTcpPort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private int FreeUdpPort()
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
}
