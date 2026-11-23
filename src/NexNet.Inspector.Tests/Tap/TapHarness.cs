using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Tap;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.Inspector.Tests.Tap;

public enum TapTransport
{
    Uds,
    Tcp,
    TcpTls,
}

/// <summary>An inspected in-process server with an inspector, its endpoint, and helpers for clients and viewers.</summary>
internal sealed class TapHarness : IAsyncDisposable
{
    public static readonly byte[] EndpointToken = "endpoint-secret-token"u8.ToArray();

    private readonly List<IAsyncDisposable> _owned = [];
    private readonly List<string> _socketFiles = [];
    private readonly TapTransport _transport;
    private readonly ServerConfig _serverConfig;
    private int _port;
    private string? _udsPath;
    private string? _endpointPath;

    /// <param name="transport">The inspected server's transport.</param>
    /// <param name="options">The inspector's options.</param>
    /// <param name="authenticate">True to make clients authenticate.</param>
    /// <param name="wrapTap">Puts a test's own tap in front of the inspector, to watch what the session gets back from it.</param>
    public TapHarness(TapTransport transport, InspectorOptions? options = null, bool authenticate = false,
        Func<INexusTap, INexusTap>? wrapTap = null)
    {
        _transport = transport;
        _serverConfig = CreateServerConfig();
        _serverConfig.Authenticate = authenticate;
        Inspector = _serverConfig.UseInspector<TapServerNexus>(options);
        if (wrapTap != null)
            _serverConfig.Tap = wrapTap(Inspector);
        Server = TapServerNexus.CreateServer(_serverConfig, () => new TapServerNexus());
        Inspector.UseSnapshotsOf(Server);
    }

    public NexusInspector Inspector { get; }

    public NexusServer<TapServerNexus, TapServerNexus.ClientProxy> Server { get; }

    public InspectorEndpoint? Endpoint { get; private set; }

    public Task StartAsync() => Server.StartAsync();

    /// <summary>The endpoint's address in nexnet-tap form (tcp://, tls:// or uds:).</summary>
    public string? EndpointAddress { get; private set; }

    public async Task<InspectorEndpoint> StartEndpointAsync(TapTransport transport = TapTransport.Uds)
    {
        ServerConfig config;
        switch (transport)
        {
            case TapTransport.Uds:
                _endpointPath = NewSocketPath();
                config = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(_endpointPath) };
                EndpointAddress = "uds:" + _endpointPath;
                break;
            case TapTransport.Tcp:
                var port = FreePort();
                config = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, port) };
                EndpointAddress = $"tcp://127.0.0.1:{port}";
                break;
            default:
                var tlsPort = FreePort();
                config = new TcpTlsServerConfig
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, tlsPort),
                    SslServerAuthenticationOptions = ServerTlsOptions(),
                };
                EndpointAddress = $"tls://127.0.0.1:{tlsPort}";
                break;
        }

        Endpoint = await Inspector.StartEndpointAsync(config, EndpointToken);
        return Endpoint;
    }

    public async Task<InspectorClient> ConnectViewerAsync(byte[]? token = null)
    {
        if (_endpointPath is null)
            throw new InvalidOperationException("ConnectViewerAsync needs a UDS endpoint.");
        var viewer = await InspectorClient.ConnectAsync(
            new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(_endpointPath) }, token ?? EndpointToken);
        _owned.Add(viewer);
        return viewer;
    }

    public ClientConfig CreateClientConfig()
    {
        ClientConfig config = _transport switch
        {
            TapTransport.Uds => new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(_udsPath!) },
            TapTransport.Tcp => new TcpClientConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, _port) },
            _ => new TcpTlsClientConfig
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, _port),
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            },
        };

        // No pings or idle timeouts during a test: the tests count every transport op.
        config.PingInterval = 600_000;
        config.Timeout = 300_000;
        return config;
    }

    public async Task<NexusClient<TapClientNexus, TapClientNexus.ServerProxy>> ConnectClientAsync(byte[]? token = null)
    {
        var config = CreateClientConfig();
        if (token != null)
            config.Authenticate = () => token;
        var client = TapClientNexus.CreateClient(config, new TapClientNexus());
        _owned.Add(client);
        await client.ConnectAsync();
        return client;
    }

    /// <summary>The newest live tapped session.</summary>
    public async Task<InspectorSessionInfo> WaitForSessionAsync(int count = 1)
    {
        for (var i = 0; i < 500; i++)
        {
            var sessions = Inspector.GetSessions();
            if (sessions.Length >= count && sessions.All(s => s.State == InspectorSessionState.Connected))
                return sessions[^1];
            await Task.Delay(10);
        }

        throw new TimeoutException("The session did not connect.");
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                await _owned[i].DisposeAsync();
            }
            catch (Exception)
            {
                // Teardown of a connection the test already ended.
            }
        }

        if (Endpoint != null)
            await Endpoint.DisposeAsync();
        await Server.DisposeAsync();
        await Inspector.DisposeAsync();
        foreach (var path in _socketFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private ServerConfig CreateServerConfig()
    {
        ServerConfig config;
        switch (_transport)
        {
            case TapTransport.Uds:
                _udsPath = NewSocketPath();
                config = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(_udsPath) };
                break;
            case TapTransport.Tcp:
                _port = FreePort();
                config = new TcpServerConfig { EndPoint = new IPEndPoint(IPAddress.Loopback, _port), TcpNoDelay = true };
                break;
            default:
                _port = FreePort();
                config = new TcpTlsServerConfig
                {
                    EndPoint = new IPEndPoint(IPAddress.Loopback, _port),
                    TcpNoDelay = true,
                    SslServerAuthenticationOptions = ServerTlsOptions(),
                };
                break;
        }

        config.Timeout = 300_000;
        return config;
    }

    private static SslServerAuthenticationOptions ServerTlsOptions() => new()
    {
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ClientCertificateRequired = false,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(AppContext.BaseDirectory, "certs", "server.pfx"), "certPass"),
    };

    private string NewSocketPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nntap-{Guid.NewGuid():N}.sock");
        _socketFiles.Add(path);
        return path;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
