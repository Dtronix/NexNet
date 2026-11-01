using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using NexNet.Quic;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Client certificates, server certificate pinning and <see cref="NexNet.Invocation.ISessionContext.RemoteCertificate"/>
/// over every TLS transport.
/// </summary>
internal class TlsCertificateTests : BaseTests
{
    private X509Certificate2 ServerCertificate = null!;
    private X509Certificate2 ClientCertificate = null!;
    private string ServerFingerprint = null!;

    [OneTimeSetUp]
    public void LoadCertificates()
    {
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certPass");
        ServerFingerprint = ServerCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        ClientCertificate = CreateClientCertificate("nexnet-test-client");
    }

    public override void OneTimeTearDown()
    {
        ServerCertificate.Dispose();
        ClientCertificate.Dispose();
        base.OneTimeTearDown();
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
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

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(await serverSaw.Task.Timeout(1), Is.EqualTo(ClientCertificate.Thumbprint));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
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

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(await serverSaw.Task.Timeout(1), Is.Null);
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task ClientSession_SeesServerCertificate(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, AcceptAny);

        var server = CreateServer(serverConfig, null);
        var (client, clientNexus) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(clientNexus.Context.RemoteCertificate?.Thumbprint, Is.EqualTo(ServerCertificate.Thumbprint));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    [TestCase(Type.TcpTls)]
    public async Task PinnedFingerprint_AcceptsServerCertificate(Type type)
    {
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, ClientCertificate, Pin(ServerFingerprint));

        var server = CreateServer(serverConfig, null);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    [TestCase(Type.TcpTls)]
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

        await server.StartAsync().Timeout(1);

        var exception = await Assert.ThrowsAsync<TransportException>(() => client.ConnectAsync().Timeout(2));
        Assert.That(exception!.Error, Is.EqualTo(TransportError.AuthenticationError));
        Assert.That(sessionCreated, Is.False);
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task UntrustedServerCertificate_RejectedWithoutCallback(Type type)
    {
        // server.pfx is self-signed, so the default chain validation must reject it.
        var (serverConfig, clientConfig) = CreateTlsConfigs(type, null, null);

        var server = CreateServer(serverConfig, null);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);

        var exception = await Assert.ThrowsAsync<TransportException>(() => client.ConnectAsync().Timeout(2));
        Assert.That(exception!.Error, Is.EqualTo(TransportError.AuthenticationError));
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(await hostSeen.Task.Timeout(1), Is.EqualTo($"127.0.0.1:{CurrentTcpPort}"));
    }

    private static RemoteCertificateValidationCallback AcceptAny => (_, _, _, _) => true;

    private static RemoteCertificateValidationCallback Pin(string sha256Hex) => (_, certificate, _, _) =>
        certificate is X509Certificate2 cert
        && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), sha256Hex, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Creates server and client configs for <paramref name="type"/> where the server presents server.pfx and
    /// accepts any client certificate, and the client presents <paramref name="clientCertificate"/> and judges the
    /// server's certificate with <paramref name="validation"/> (null for the default chain validation).
    /// </summary>
    private (ServerConfig server, ClientConfig client) CreateTlsConfigs(
        Type type,
        X509Certificate2? clientCertificate,
        RemoteCertificateValidationCallback? validation)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);

        var clientOptions = new SslClientAuthenticationOptions
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = validation,
            ClientCertificates = clientCertificate == null ? null : new X509CertificateCollection { clientCertificate },
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
                clientOptions.EnabledSslProtocols = SslProtocols.Tls13;
                tlsClient.SslClientAuthenticationOptions = clientOptions;
                break;

            case (QuicServerConfig quicServer, QuicClientConfig quicClient):
                quicServer.SslServerAuthenticationOptions.ClientCertificateRequired = clientCertificate != null;
                quicServer.SslServerAuthenticationOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                clientOptions.TargetHost = "localhost";
                clientOptions.EnabledSslProtocols = SslProtocols.Tls13;
                quicClient.SslClientAuthenticationOptions = clientOptions;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        return (serverConfig, clientConfig);
    }

    private static X509Certificate2 CreateClientCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Windows TLS cannot use an ephemeral key for a client certificate; round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
