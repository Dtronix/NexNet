using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Quic;
using NexNet.Transports;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace NexNet.IntegrationTests;

public class BaseTests
{
    public enum Type
    {
        Uds,
        Tcp,
        TcpTls,
        Quic
    }

    private int _counter;
    private DirectoryInfo? _socketDirectory;
    protected UnixDomainSocketEndPoint? CurrentPath;
    //private ConsoleLogger _logger;
    protected int? CurrentTcpPort;
    protected int? CurrentUdpPort;
    //protected List<ConsoleLogger> Loggers = new List<ConsoleLogger>();
    protected List<INexusServer> Servers = new List<INexusServer>();
    protected List<INexusClient> Clients = new List<INexusClient>();
    private ConsoleLogger _logger = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {

        //_logger = new ConsoleLogger();
        Trace.Listeners.Add(new ConsoleTraceListener());
        _socketDirectory = Directory.CreateTempSubdirectory("socketTests");

    }

    [OneTimeTearDown]
    public virtual void OneTimeTearDown()
    {
        _socketDirectory?.Delete(true);
        Trace.Flush();
    }

    [SetUp]
    public virtual void SetUp()
    {
        _logger = new ConsoleLogger();
    }

    [TearDown]
    public virtual void TearDown()
    {
        CurrentPath = null;
        CurrentTcpPort = null;
        CurrentUdpPort = null;
        _logger.LogEnabled = false;
        
        foreach (var nexusClient in Clients)
        {
            if(nexusClient.State != ConnectionState.Connected)
                continue;

            _ = nexusClient.DisconnectAsync();
            //nexusClient.DisconnectedTask?.Wait();
        }
        Clients.Clear();

        foreach (var nexusServer in Servers)
        {
            if(!nexusServer.IsStarted)
                continue;

            _ = nexusServer.StopAsync();
            //nexusServer.StoppedTask?.Wait();
        }

        Servers.Clear();
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
        else if (type == Type.Tcp)
        {
            CurrentTcpPort ??= FreeTcpPort();

            return new TcpServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentTcpPort.Value),
                Logger = logger,
                TcpNoDelay = true
            };
        }
        else if (type == Type.TcpTls)
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
        else if (type == Type.Quic)
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


        throw new InvalidOperationException();
    }

    protected ServerConfig CreateServerConfig(Type type, bool log = false)
    {
        var logger = log ? _logger.CreateLogger(null, "SV") : null;
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
        else if (type == Type.Tcp)
        {
            CurrentTcpPort ??= FreeTcpPort();

            return new TcpClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, CurrentTcpPort.Value),
                Logger = logger,
            };
        }
        else if (type == Type.TcpTls)
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
        else if (type == Type.Quic)
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

        throw new InvalidOperationException();
    }

    protected ClientConfig CreateClientConfig(Type type, bool log = false)
    {
        var logger = log ? _logger.CreateLogger(null, "CL") : null;

        return CreateClientConfigWithLog(type, logger);
    }


    protected (NexusServer<ServerNexus, ServerNexus.ClientProxy> server, ServerNexus serverNexus, NexusClient<ClientNexus, ClientNexus.ServerProxy> client, ClientNexus clientNexus)
        CreateServerClient(ServerConfig sConfig, ClientConfig cConfig)
    {
        var serverNexus = new ServerNexus();
        var clientNexus = new ClientNexus();
        var server = ServerNexus.CreateServer(sConfig, () => serverNexus);
        var client = ClientNexus.CreateClient(cConfig, clientNexus);
        Servers.Add(server);
        Clients.Add(client);

        return (server, serverNexus, client, clientNexus);
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

        Assert.AreEqual(typeof(T), thrown?.GetType());
    }
}
