using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Interop.InProc;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Quic;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Client certificates, server certificate trust (custom CA, host names, SPKI pinning) and
/// <see cref="NexNet.Invocation.ISessionContext.RemoteCertificate"/> over every TLS transport, on the client matrix
/// (P01d). The client under test comes from <see cref="Harness"/> with a <see cref="ClientSpec"/>; the server is the C#
/// server of the test.
/// </summary>
/// <remarks>
/// The QUIC rows are C#-only rows (<see cref="ClientMatrixAttribute.CSharpTransports"/>): no other client language has
/// QUIC. Browsers control none of these TLS settings: their rows are skipped with the capability they lack.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class TlsCertificateTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    // Loaded once for the class; TUnit creates an instance per test.
    private static X509Certificate2 ServerCertificate = null!;
    private static X509Certificate2 ClientCertificate = null!;
    private static X509Certificate2 TestCa = null!;
    private static string ServerFingerprint = null!;
    private static string ServerSpkiPin = null!;

    [Before(HookType.Class)]
    public static void LoadCertificates()
    {
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "server.pfx"), "certPass");
        ServerFingerprint = ServerCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        ServerSpkiPin = Convert.ToBase64String(SHA256.HashData(ServerCertificate.PublicKey.ExportSubjectPublicKeyInfo()));
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

        // The Node, Java and browser pin tests read the SPKI pin from certs/README.md.
        await Assert.That(File.ReadAllText(Path.Combine("certs", "README.md"))).Contains($"`{ServerSpkiPin}`");
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.CustomCa, CSharpTransports = [ClientTransport.Quic])]
    public async Task TrustedTestCa_AcceptsServerCertificate(ClientTarget target)
    {
        // Full validation: chain to the explicitly trusted test CA plus host name checks against the SAN
        // (the IP SAN 127.0.0.1 for the HTTP transports, the DNS SAN localhost for TcpTls). No callback.
        var spec = await StartTlsServerAsync(target, clientCertificate: false);
        spec.CaPem = TestCaPem;
        spec.TlsServerName = target.Transport == ClientTransport.TcpTls ? "localhost" : null;
        await using var client = await CreateClientAsync(target, spec);

        await client.ConnectAsync().Timeout(T(2));

        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await EndAsync(client);
    }

    // The HTTP transports always validate against the URL host (SocketsHttpHandler ignores TargetHost).
    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls], Requires = ClientCapabilities.CustomCa, CSharpTransports = [ClientTransport.Quic])]
    public async Task TrustedTestCa_RejectsWrongHostName(ClientTarget target)
    {
        var spec = await StartTlsServerAsync(target, clientCertificate: false);
        spec.CaPem = TestCaPem;
        spec.TlsServerName = "nexnet.invalid";
        await using var client = await CreateClientAsync(target, spec);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(2)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.ClientCertificate | ClientCapabilities.CustomCa, CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientCertificate_ReachesServerSession(ClientTarget target)
    {
        var serverSaw = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = await StartTlsServerAsync(target, clientCertificate: true, nexus => nexus.OnConnectedEvent = n =>
        {
            serverSaw.TrySetResult(n.Context.RemoteCertificate?.Thumbprint);
            return ValueTask.CompletedTask;
        });
        spec.CaPem = TestCaPem;
        PresentClientCertificate(spec);
        await using var client = await CreateClientAsync(target, spec);

        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(await serverSaw.Task.Timeout(T(1))).IsEqualTo(ClientCertificate.Thumbprint);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.CustomCa, CSharpTransports = [ClientTransport.Quic])]
    public async Task NoClientCertificate_ServerSessionHasNone(ClientTarget target)
    {
        var serverSaw = new TaskCompletionSource<X509Certificate2?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = await StartTlsServerAsync(target, clientCertificate: false, nexus => nexus.OnConnectedEvent = n =>
        {
            serverSaw.TrySetResult(n.Context.RemoteCertificate);
            return ValueTask.CompletedTask;
        });
        spec.CaPem = TestCaPem;
        await using var client = await CreateClientAsync(target, spec);

        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(await serverSaw.Task.Timeout(T(1))).IsNull();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.RemoteCertificate | ClientCapabilities.CustomCa, CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientSession_SeesServerCertificate(ClientTarget target)
    {
        var spec = await StartTlsServerAsync(target, clientCertificate: false);
        spec.CaPem = TestCaPem;
        await using var client = await CreateClientAsync(target, spec);

        // IClientUnderTest cannot read the client's remote certificate yet; only the C# client is checked.
        if (client is not InProcClientDriver<ClientNexus, ClientNexus.ServerProxy> inProc)
        {
            Skip.Test("IClientUnderTest has no query for the client's remote certificate yet (P01d follow-up).");
            return;
        }

        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(inProc.Nexus.Context.RemoteCertificate?.Thumbprint).IsEqualTo(ServerCertificate.Thumbprint);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.Pinning | ClientCapabilities.ClientCertificate, CSharpTransports = [ClientTransport.Quic])]
    public async Task PinnedFingerprint_AcceptsServerCertificate(ClientTarget target)
    {
        var spec = await StartTlsServerAsync(target, clientCertificate: true);
        spec.PinSha256 = ServerSpkiPin;
        PresentClientCertificate(spec);
        await using var client = await CreateClientAsync(target, spec);

        await client.ConnectAsync().Timeout(T(2));

        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.Pinning | ClientCapabilities.ClientCertificate, CSharpTransports = [ClientTransport.Quic])]
    public async Task PinnedFingerprint_RejectsWrongCertificate(ClientTarget target)
    {
        var sessionCreated = false;
        var spec = await StartTlsServerAsync(target, clientCertificate: true, nexus => nexus.OnConnectedEvent = _ =>
        {
            sessionCreated = true;
            return ValueTask.CompletedTask;
        });
        // The pin alone decides trust: the test CA (trusted by the spec) does not rescue a wrong pin.
        spec.PinSha256 = Convert.ToBase64String(SHA256.HashData("not the server"u8));
        PresentClientCertificate(spec);
        await using var client = await CreateClientAsync(target, spec);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(2)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
        await Assert.That(sessionCreated).IsFalse();
        await EndAsync(client);
    }

    // A client that cannot choose what it trusts (a browser: its launcher decides) cannot run this.
    [Test]
    [ClientMatrix(Transports = [ClientTransport.TcpTls, ClientTransport.HttpSocketTls, ClientTransport.WebSocketTls], Requires = ClientCapabilities.CustomCa, CSharpTransports = [ClientTransport.Quic])]
    public async Task UntrustedServerCertificate_RejectedWithoutCallback(ClientTarget target)
    {
        // server.pfx is issued by the test CA, which the system trust store does not hold, so the default chain
        // validation must reject it.
        var spec = await StartTlsServerAsync(target, clientCertificate: false);
        spec.CaPem = null;
        await using var client = await CreateClientAsync(target, spec);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(2)));
        await Assert.That(exception!.Error).IsEqualTo(TransportError.AuthenticationError);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public async Task HostHeader_IncludesNonDefaultPort(ClientTarget target)
    {
        var type = ToType(target.Transport);
        var serverConfig = CreateServerConfig(type);

        var hostSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, null, null, app => app.Use((context, next) =>
        {
            hostSeen.TrySetResult(context.Request.Host.Value ?? "");
            return next(context);
        }));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(
            target, Spec(CreateClientConfig(type), target.Transport, Logger));

        await client.ConnectAsync().Timeout(T(2));

        await Assert.That(await hostSeen.Task.Timeout(T(1))).IsEqualTo($"127.0.0.1:{CurrentTcpPort}");
        await EndAsync(client);
    }

    /// <summary>Creates (not connects) the client of <paramref name="target"/> of the basic test nexus.</summary>
    private static Task<IClientUnderTest> CreateClientAsync(ClientTarget target, ClientSpec spec)
        => Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);

    private static string TestCaPem => File.ReadAllText(Path.Combine("certs", "test-ca.pem"));

    /// <summary>The client of <paramref name="spec"/> presents certs/client.pem.</summary>
    private static void PresentClientCertificate(ClientSpec spec)
    {
        spec.ClientCertificatePem = File.ReadAllText(Path.Combine("certs", "client.pem"));
        spec.ClientKeyPem = File.ReadAllText(Path.Combine("certs", "client.key"));
    }

    /// <summary>
    /// Starts a <see cref="ServerNexus"/> server for <paramref name="target"/>'s TLS transport that presents server.pfx
    /// and, with <paramref name="clientCertificate"/>, asks for (TcpTls: requires) a client certificate and accepts any.
    /// Returns the spec of a client of that server that trusts nothing yet (no CA, no pin) and presents no certificate.
    /// </summary>
    private async Task<ClientSpec> StartTlsServerAsync(ClientTarget target, bool clientCertificate, Action<ServerNexus>? nexusCreated = null)
    {
        // The HTTPS listener is set up here (with client certificates), so the ASP.NET servers start as plain HTTP ones.
        var type = target.Transport switch
        {
            ClientTransport.TcpTls => Type.TcpTls,
            ClientTransport.HttpSocketTls => Type.HttpSocket,
            ClientTransport.WebSocketTls => Type.WebSocket,
            ClientTransport.Quic => Type.Quic,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Transport, "Not a TLS transport."),
        };
        var serverConfig = CreateServerConfig(type);
        switch (serverConfig)
        {
            case AspServerConfig:
                AspListenOptions = listen => listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = ServerCertificate,
                    ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                    ClientCertificateValidation = (_, _, _) => true,
                });
                break;

            case TcpTlsServerConfig tlsServer:
                tlsServer.SslServerAuthenticationOptions.ClientCertificateRequired = clientCertificate;
                tlsServer.SslServerAuthenticationOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                break;

            case QuicServerConfig quicServer:
                quicServer.SslServerAuthenticationOptions.ClientCertificateRequired = clientCertificate;
                quicServer.SslServerAuthenticationOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                break;
        }

        var server = CreateServer(serverConfig, nexusCreated);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var address = CreateClientConfig(type) switch
        {
            HttpClientConfig http => new UriBuilder(http.Url) { Scheme = http.Url.Scheme == "ws" ? "wss" : "https" }.Uri.ToString(),
            var other => Address(other),
        };
        return new ClientSpec { Transport = target.Transport, Address = address, Logger = Logger };
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
}
