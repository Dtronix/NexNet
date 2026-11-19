using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Quic;
using NexNet.RateLimiting;
using NexNet.Transports;

#pragma warning disable VSTHRD200
#pragma warning disable CA1416

namespace NexNet.IntegrationTests;

/// <summary>
/// Transport handshakes (TLS, the QUIC stream accept) run off the accept loop: a slow or stalled peer must not hold
/// up other accepts, and each pending handshake counts against MaxPendingHandshakes and the rate limiter.
/// </summary>
[NotInParallel]
internal class HandshakeOffLoopTests : BaseTests
{
    [Test]
    public async Task TcpTls_StalledClients_DoNotDelayNextClient()
    {
        var serverConfig = CreateServerConfig(Type.TcpTls);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        // Ten connections that never send a ClientHello; each would hold a sequential accept loop for the full
        // SslConnectionTimeout (5 s).
        var stalled = await OpenStalledTcpClients(((TcpTlsServerConfig)serverConfig).EndPoint, 10);
        try
        {
            await WaitFor(() => server.GetSnapshot().PendingHandshakes == 10, 2,
                "Each stalled TLS connection should be accepted and hold a pending handshake slot.");

            var (client, _) = CreateClient(CreateClientConfig(Type.TcpTls));
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync().Timeout(5);
            sw.Stop();

            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
            await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1)).Because("Stalled TLS handshakes must not delay the next client.");
        }
        finally
        {
            DisposeAll(stalled);
        }
    }

    [Test]
    public async Task TcpTls_StalledHandshake_TimesOutAndFreesItsSlot()
    {
        var serverConfig = (TcpTlsServerConfig)CreateServerConfig(Type.TcpTls);
        serverConfig.SslConnectionTimeout = 300;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var stalled = await OpenStalledTcpClients(serverConfig.EndPoint, 1);
        try
        {
            await WaitFor(() => server.GetSnapshot().PendingHandshakes == 1, 2,
                "The stalled TLS connection should hold a pending handshake slot.");
            await WaitFor(() => server.GetSnapshot().PendingHandshakes == 0, 3,
                "The TLS handshake timeout should free the slot.");
        }
        finally
        {
            DisposeAll(stalled);
        }
    }

    [Test]
    public async Task TcpTls_StalledHandshakes_CountAgainstMaxPendingHandshakes()
    {
        var serverConfig = CreateServerConfig(Type.TcpTls);
        serverConfig.MaxPendingHandshakes = 2;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var endPoint = ((TcpTlsServerConfig)serverConfig).EndPoint;
        var stalled = await OpenStalledTcpClients(endPoint, 2);
        try
        {
            await WaitFor(() => server.GetSnapshot().PendingHandshakes == 2, 2,
                "The stalled TLS connections should hold both slots.");

            // A third connection is closed straight away, long before the TLS handshake timeout.
            using var rejected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await rejected.ConnectAsync(endPoint).Timeout(1);
            await Assert.That(await ReceiveUntilClosed(rejected).Timeout(1)).IsEqualTo(0);
        }
        finally
        {
            DisposeAll(stalled);
        }
    }

    [Test]
    public async Task TcpTls_RateLimiterSlot_IsReleasedWhenTheHandshakeFails()
    {
        var serverConfig = (TcpTlsServerConfig)CreateServerConfig(Type.TcpTls);
        serverConfig.SslConnectionTimeout = 300;
        serverConfig.RateLimiting = new ConnectionRateLimitConfig
        {
            MaxConcurrentConnections = 1,
            GlobalConnectionsPerSecond = 0,
            BanDurationSeconds = 0,
        };
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        // The stalled connection takes the only rate limiter slot before its handshake runs.
        var stalled = await OpenStalledTcpClients(serverConfig.EndPoint, 1);
        try
        {
            await WaitFor(() => server.GetSnapshot().RateLimiter?.CurrentConnections == 1, 2,
                "The stalled TLS connection should hold the rate limiter slot.");

            // Its handshake times out, which must give the slot back.
            await WaitFor(() => server.GetSnapshot().RateLimiter?.CurrentConnections == 0, 3,
                "A failed TLS handshake should release its rate limiter slot.");

            var (client, _) = CreateClient(CreateClientConfig(Type.TcpTls));
            await client.ConnectAsync().Timeout(5);
            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        }
        finally
        {
            DisposeAll(stalled);
        }
    }

    [Test]
    public async Task Quic_PeerThatOpensNoStream_DoesNotBlockOthers()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var serverConfig = (QuicServerConfig)CreateServerConfig(Type.Quic);
        serverConfig.HandshakeTimeout = 500;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        // QUIC connections that complete the TLS handshake but never open the NexNet stream.
        var silent = new List<QuicConnection>();
        try
        {
            for (var i = 0; i < 3; i++)
                silent.Add(await ConnectSilentQuicPeer(serverConfig.EndPoint).Timeout(5));

            await WaitFor(() => server.GetSnapshot().PendingHandshakes == 3, 2,
                "Each silent QUIC connection should hold a pending handshake slot.");

            var (client, _) = CreateClient(CreateClientConfig(Type.Quic));
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync().Timeout(5);
            sw.Stop();

            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
            await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1)).Because("A QUIC peer that opens no stream must not delay the next client.");

            // The stream accept is bounded by HandshakeTimeout, which frees the slots.
            await WaitFor(() => server.GetSnapshot().PendingHandshakes == 0, 3,
                "The stream accept timeout should free the silent connections' slots.");
        }
        finally
        {
            foreach (var connection in silent)
                await connection.DisposeAsync();
        }
    }

    [Test]
    public async Task Quic_ClientStreamOpenFails_ClosesTheConnection()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var serverConfig = (QuicServerConfig)CreateServerConfig(Type.Quic);

        // A raw QUIC server that accepts the connection but grants no bidirectional streams, so the client's
        // stream open waits until its connection timeout.
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = serverConfig.EndPoint,
            ApplicationProtocols = [QuicHelpers.Alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0x0A,
                DefaultCloseErrorCode = 0x0B,
                MaxInboundBidirectionalStreams = 0,
                ServerAuthenticationOptions = QuicHelpers.WithNexNetAlpn(serverConfig.SslServerAuthenticationOptions),
            }),
        }).Timeout(1);

        var clientConfig = (QuicClientConfig)CreateClientConfig(Type.Quic);
        clientConfig.ConnectionTimeout = 300;

        var connectTask = QuicTransport.ConnectAsync(clientConfig, CancellationToken.None).AsTask();
        await using var serverConnection = await listener.AcceptConnectionAsync().AsTask().Timeout(5);

        await Assert.ThrowsAsync<Exception>(async () => await connectTask.Timeout(5));

        // The client must close its connection once the stream open fails; otherwise the server only notices
        // at its idle timeout (30 s by default).
        var sw = Stopwatch.StartNew();
        var closed = await serverConnection.AcceptInboundStreamAsync().AsTask()
            .ContinueWith(t => t.Exception?.InnerException, TaskScheduler.Default).Timeout(5);
        await Assert.That(closed).IsAssignableTo<QuicException>().Because("The server should see the connection closed.");
        await Assert.That(((QuicException)closed!).QuicError).IsEqualTo(QuicError.ConnectionAborted);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Quic_SharedSslOptions_AlpnStaysUnchanged()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var serverConfig = (QuicServerConfig)CreateServerConfig(Type.Quic);
        var serverOptions = serverConfig.SslServerAuthenticationOptions;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var clientConfig = (QuicClientConfig)CreateClientConfig(Type.Quic);
        var clientOptions = clientConfig.SslClientAuthenticationOptions;
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(5);

        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(serverOptions.ApplicationProtocols).IsNull().Because("The QUIC listener must not set its ALPN on the caller's (possibly shared) options.");
        await Assert.That(clientOptions.ApplicationProtocols).IsNull().Because("The QUIC client must not set its ALPN on the caller's options.");
    }

    [Test]
    public async Task Quic_ServerOptionsCopy_CopiesEveryPropertyButAlpn()
    {
        var original = new SslServerAuthenticationOptions
        {
            AllowRenegotiation = true,
            AllowTlsResume = false,
            AllowRsaPssPadding = false,
            AllowRsaPkcs1Padding = false,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
            CertificateChainPolicy = new X509ChainPolicy(),
            CertificateRevocationCheckMode = X509RevocationMode.Offline,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("certs", "server.pfx"), "certPass"),
            ServerCertificateSelectionCallback = (_, _) => null!,
        };

        var copy = QuicHelpers.WithNexNetAlpn(original);

        await AssertCopied(original, copy);
        await Assert.That(copy.ApplicationProtocols).IsEquivalentTo(new[] { QuicHelpers.Alpn }, CollectionOrdering.Matching);
        await Assert.That(original.ApplicationProtocols).IsEquivalentTo(new[] { SslApplicationProtocol.Http2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Quic_ClientOptionsCopy_CopiesEveryPropertyButAlpn()
    {
        var original = new SslClientAuthenticationOptions
        {
            AllowRenegotiation = false,
            AllowTlsResume = false,
            AllowRsaPssPadding = false,
            AllowRsaPkcs1Padding = false,
            ApplicationProtocols = [SslApplicationProtocol.Http2],
            CertificateChainPolicy = new X509ChainPolicy(),
            CertificateRevocationCheckMode = X509RevocationMode.Offline,
            ClientCertificates = new X509CertificateCollection(),
            EnabledSslProtocols = SslProtocols.Tls13,
            LocalCertificateSelectionCallback = (_, _, _, _, _) => null!,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            TargetHost = "example.test",
        };

        var copy = QuicHelpers.WithNexNetAlpn(original);

        await AssertCopied(original, copy);
        await Assert.That(copy.ApplicationProtocols).IsEquivalentTo(new[] { QuicHelpers.Alpn }, CollectionOrdering.Matching);
        await Assert.That(original.ApplicationProtocols).IsEquivalentTo(new[] { SslApplicationProtocol.Http2 }, CollectionOrdering.Matching);
    }

    /// <summary>
    /// Every public settable property except ApplicationProtocols must be copied, including properties a future
    /// .NET version adds: a property the copy misses keeps its default and fails here whenever the original's value
    /// differs from it.
    /// </summary>
    private static async Task AssertCopied<T>(T original, T copy) where T : class, new()
    {
        var defaults = new T();
        var uncovered = new List<string>();
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.Name == nameof(SslServerAuthenticationOptions.ApplicationProtocols))
                continue;

            var expected = property.GetValue(original);
            var actual = property.GetValue(copy);
            await Assert.That(ReferenceEquals(actual, expected) || Equals(actual, expected)).IsTrue().Because(property.Name);

            // A property this test leaves at its default proves nothing about the copy; list it so a new one is
            // noticed and given a value above.
            if (Equals(expected, property.GetValue(defaults)))
                uncovered.Add(property.Name);
        }

        // Properties that can not be given a non-default value on every platform, or whose only non-default values
        // are obsolete (EncryptionPolicy).
        uncovered.Remove("CipherSuitesPolicy");
        uncovered.Remove("EncryptionPolicy");
        uncovered.Remove("ServerCertificateContext");
        uncovered.Remove("ClientCertificateContext");
        await Assert.That(uncovered).IsEmpty().Because("Give these properties a non-default value in the test.");
    }

    private static async Task<QuicConnection> ConnectSilentQuicPeer(IPEndPoint endPoint)
    {
        return await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = endPoint,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol("nn1")],
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        });
    }

    private static async Task<List<Socket>> OpenStalledTcpClients(EndPoint endPoint, int count)
    {
        var sockets = new List<Socket>(count);
        for (var i = 0; i < count; i++)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sockets.Add(socket);
            await socket.ConnectAsync(endPoint).Timeout(1);
        }

        return sockets;
    }

    private static void DisposeAll(List<Socket> sockets)
    {
        foreach (var socket in sockets)
            socket.Dispose();
    }

    /// <summary>
    /// Reads until the peer closes; returns the number of bytes received. A reset counts as closed.
    /// </summary>
    private static async Task<int> ReceiveUntilClosed(Socket socket)
    {
        var total = 0;
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer);
                if (read == 0)
                    return total;
                total += read;
            }
        }
        catch (SocketException)
        {
            return total;
        }
    }

    private static async Task WaitFor(Func<bool> condition, double seconds, string message)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                Fail.Test(message);
            await Task.Delay(10);
        }
    }
}