using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

public class BaseTests
{
    public enum Type
    {
        Uds,
        Tcp,
        TcpTls
    }

    private int _counter;
    private DirectoryInfo? _socketDirectory;
    protected UnixDomainSocketEndPoint? CurrentPath;
    //private ConsoleLogger _logger;
    protected int? CurrentTcpPort;

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

    [TearDown]
    public virtual void TearDown()
    {
        CurrentPath = null;
        CurrentTcpPort = null;
    }
    protected ServerConfig CreateServerConfig(Type type, bool log = false)
    {
        var logger = log ? new ConsoleLogger("SV") : null;
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

        throw new InvalidOperationException();
    }

    protected ClientConfig CreateClientConfig(Type type, bool log = false)
    {
        var logger = log ? new ConsoleLogger("CL") : null;
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
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
        }

        throw new InvalidOperationException();
    }


    protected (NexNetServer<ServerHub, ClientHubProxyImpl> server, ServerHub serverHub, NexNetClient<ClientHub, ServerHubProxyImpl> client, ClientHub clientHub)
        CreateServerClient(ServerConfig sConfig, ClientConfig cConfig)
    {
        var serverHub = new ServerHub();
        var clientHub = new ClientHub();
        var server = ServerHub.CreateServer(sConfig, () => serverHub);
        var client = ClientHub.CreateClient(cConfig, clientHub);

        return (server, serverHub, client, clientHub);
    }

    private int FreeTcpPort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
