using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using NexNet.Quic;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Client certificates, server certificate pinning and <see cref="NexNet.Invocation.ISessionContext.RemoteCertificate"/>
/// over every TLS transport.
/// </summary>
internal class TlsCertificateTests : BaseTests
{
    // Loaded once for the class; TUnit creates an instance per test.
    private static X509Certificate2 ServerCertificate = null!;
    private static X509Certificate2 ClientCertificate = null!;
    private static X509Certificate2 TestCa = null!;
    private static string ServerFingerprint = null!;

    [Before(HookType.Class)]
    public static void LoadCertificates()
    {
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "server.pfx"), "certPass");
        ServerFingerprint = ServerCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        ClientCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "client.pfx"), "certPass");
        TestCa = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine("certs", "test-ca.pem")));
    }

    [After(HookType.Class)]
    public static void DisposeCertificates()
    {
        ServerCertificate.Dispose();
        ClientCertificate.Dispose();
        TestCa.Dispose();
    }

    [Test]
    public async Task ServerCertificate_HasLoopbackSubjectAltNames()
    {
        var san = ServerCertificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        CallbackAssert.Equal(san.EnumerateDnsNames(), new[] { "localhost" });
        CallbackAssert.Equal(san.EnumerateIPAddresses(), new[] { IPAddress.Loopback, IPAddress.IPv6Loopback });

        var eku = ServerCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        CallbackAssert.Equal(eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value), new[] { "1.3.6.1.5.5.7.3.1" });
    }

    [Test]
    public async Task TestCertificates_ChainToTestCaAndMatchReadmePin()
    {
        using var serverPem = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine("certs", "server.pem")));
        using var clientPem = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine("certs", "client.pem")));
        await Assert.That(serverPem.Thumbprint).IsEqualTo(ServerCertificate.Thumbprint);
        await Assert.That(clientPem.Thumbprint).IsEqualTo(ClientCertificate.Thumbprint);

        await Assert.That(BuildChain(ServerCertificate, "1.3.6.1.5.5.7.3.1")).IsTrue().Because("server leaf must chain to the test CA");
        await Assert.That(BuildChain(ClientCertificate, "1.3.6.1.5.5.7.3.2")).IsTrue().Because("client leaf must chain to the test CA");

        // Later Node, Java and browser pin tests read the SPKI pin from certs/README.md.
        var pin = Convert.ToBase64String(SHA256.HashData(ServerCertificate.PublicKey.ExportSubjectPublicKeyInfo()));
        await Assert.That(File.ReadAllText(Path.Combine("certs", "README.md"))).Contains($"`{pin}`");
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task TrustedTestCa_AcceptsServerCertificate(Type type)
    {
        // Full validation: chain to the explicitly trusted test CA plus host name checks against the SAN
        // (the IP SAN 127.0.0.1 for the HTTP transports, the DNS SAN localhost for TcpTls/QUIC). No callback.
        var targetHost = type is Type.TcpTls ? "localhost" : null;
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, null, TrustTestCa(), targetHost);

        var server = CreateServer(serverConfig, null);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    // The HTTP transports always validate against the URL host (SocketsHttpHandler ignores TargetHost).
    [Test]
    [Transports(Type.TcpTls, Type.Quic)]
    public async Task TrustedTestCa_RejectsWrongHostName(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, null, TrustTestCa(), "nexnet.invalid");

        var server = CreateServer(serverConfig, null);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(2)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task ClientCertificate_ReachesServerSession(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, ClientCertificate, AcceptAny);

        var serverSaw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnConnectedEvent = n =>
        {
            serverSaw.TrySetResult(n.Context.RemoteCertificate?.Thumbprint);
            return ValueTask.CompletedTask;
        });
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(await serverSaw.Task.Timeout(T(1))).IsEqualTo(ClientCertificate.Thumbprint);
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task NoClientCertificate_ServerSessionHasNone(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, AcceptAny);

        var serverSaw = new TaskCompletionSource<X509Certificate2?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnConnectedEvent = n =>
        {
            serverSaw.TrySetResult(n.Context.RemoteCertificate);
            return ValueTask.CompletedTask;
        });
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(await serverSaw.Task.Timeout(T(1))).IsNull();
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task ClientSession_SeesServerCertificate(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, AcceptAny);

        var server = CreateServer(serverConfig, null);
        var (client, clientNexus) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(clientNexus.Context.RemoteCertificate?.Thumbprint).IsEqualTo(ServerCertificate.Thumbprint);
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task PinnedFingerprint_AcceptsServerCertificate(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, ClientCertificate, Pin(ServerFingerprint));

        var server = CreateServer(serverConfig, null);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task PinnedFingerprint_RejectsWrongCertificate(Type type)
    {
        var wrongPin = Convert.ToHexString(SHA256.HashData("not the server"u8));
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, ClientCertificate, Pin(wrongPin));

        var sessionCreated = false;
        var server = CreateServer(serverConfig, nexus => nexus.OnConnectedEvent = _ =>
        {
            sessionCreated = true;
            return ValueTask.CompletedTask;
        });
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(2)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
        await Assert.That(sessionCreated).IsFalse();
    }

    [Test]
    [Transports(Except = new[] { Type.Uds, Type.Tcp })]
    public async Task UntrustedServerCertificate_RejectedWithoutCallback(Type type)
    {
        // server.pfx is issued by the test CA, which the system trust store does not hold, so the default chain
        // validation must reject it.
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, null);

        var server = CreateServer(serverConfig, null);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(2)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task HostHeader_IncludesNonDefaultPort(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);

        var hostSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, null, null, app => app.Use((context, next) =>
        {
            hostSeen.TrySetResult(context.Request.Host.Value ?? "");
            return next(context);
        }));
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(await hostSeen.Task.Timeout(T(1))).IsEqualTo($"127.0.0.1:{CurrentTcpPort}");
    }

    private X509ChainPolicy TrustTestCa()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(TestCa);
        return policy;
    }

    private bool BuildChain(X509Certificate2 leaf, string ekuOid)
    {
        using var chain = new X509Chain { ChainPolicy = TrustTestCa() };
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ekuOid));
        return chain.Build(leaf)
               && chain.ChainElements.Count == 2
               && chain.ChainElements[1].Certificate.Thumbprint == TestCa.Thumbprint;
    }

    private static RemoteCertificateValidationCallback AcceptAny => (_, _, _, _) => true;

    private static RemoteCertificateValidationCallback Pin(string sha256Hex) => (_, certificate, _, _) =>
        certificate is X509Certificate2 cert
        && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), sha256Hex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates server and client configs for <paramref name="type"/> where the server presents server.pfx and
    /// accepts any client certificate, and the client presents <paramref name="clientCertificate"/> and judges the
    /// server's certificate with <paramref name="validation"/> (null for the default chain validation).
    /// <paramref name="chainPolicy"/> replaces the client's chain policy (e.g. to trust the test CA) and
    /// <paramref name="targetHost"/> is the host name the TcpTls/QUIC client validates (QUIC defaults to localhost);
    /// the HTTP transports always validate against the URL host, 127.0.0.1.
    /// </summary>
    private (ServerConfig server, ClientConfig client) CreateTlsConfigs(
        Type type,
        X509Certificate2? clientCertificate,
        RemoteCertificateValidationCallback? validation,
        X509ChainPolicy? chainPolicy = null,
        string? targetHost = null)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);

        var clientOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = validation,
            ClientCertificates = clientCertificate == null ? null : new X509CertificateCollection { clientCertificate },
            CertificateChainPolicy = chainPolicy,
        };

        switch (serverConfig, clientConfig)
        {
            case (AspServerConfig, HttpClientConfig httpClient):
                AspListenOptions = listen => listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = ServerCertificate,
                    ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                    ClientCertificateValidation = (_, _, _) => true,
                });
                var builder = new UriBuilder(httpClient.Url) { Scheme = httpClient.Url.Scheme == "ws" ? "wss" : "https" };
                httpClient.Url = builder.Uri;
                httpClient.SslClientAuthenticationOptions = clientOptions;
                break;

            case (TcpTlsServerConfig tlsServer, TcpTlsClientConfig tlsClient):
                tlsServer.SslServerAuthenticationOptions.ClientCertificateRequired = clientCertificate != null;
                tlsServer.SslServerAuthenticationOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                clientOptions.TargetHost = targetHost;
                clientOptions.EnabledSslProtocols = SslProtocols.Tls13;
                tlsClient.SslClientAuthenticationOptions = clientOptions;
                break;

            case (QuicServerConfig quicServer, QuicClientConfig quicClient):
                quicServer.SslServerAuthenticationOptions.ClientCertificateRequired = clientCertificate != null;
                quicServer.SslServerAuthenticationOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                clientOptions.TargetHost = targetHost ?? "localhost";
                clientOptions.EnabledSslProtocols = SslProtocols.Tls13;
                quicClient.SslClientAuthenticationOptions = clientOptions;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        return (serverConfig, clientConfig);
    }
}