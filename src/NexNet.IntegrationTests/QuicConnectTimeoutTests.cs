using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using NexNet.Quic;
using NexNet.Transports;

#pragma warning disable VSTHRD200
#pragma warning disable CA1416

namespace NexNet.IntegrationTests;

/// <summary>
/// A QUIC connect to a peer that never answers fails within the connection timeout. A canceled
/// QuicConnection.ConnectAsync only completes about 3 s after its cancellation (msquic shuts the half-open
/// connection down first), so a client that waited for it overshot the timeout by that much.
/// </summary>
[NotInParallel]
internal class QuicConnectTimeoutTests : BaseTests
{
    private const int ConnectionTimeoutMs = 1000;

    // The overshoot was about 3 s; this allows for a loaded machine without accepting it.
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1500);

    [Test]
    public async Task Quic_ConnectToSilentPeer_FailsWithinTheConnectionTimeout()
    {
        if (!QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        // A UDP socket that receives the client's packets and never answers: no ICMP unreachable, no handshake.
        using var silentPeer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        silentPeer.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var clientConfig = (QuicClientConfig)CreateClientConfig(Type.Quic);
        clientConfig.EndPoint = silentPeer.LocalEndPoint!;

        await AssertConnectTimesOut(clientConfig);
    }

    [Test]
    public async Task Quic_ConnectToStoppedServer_FailsWithinTheConnectionTimeout()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var server = CreateServer(CreateServerConfig(Type.Quic), null);
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient(CreateClientConfig(Type.Quic));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        await server.StopAsync().Timeout(TestTimeouts.Connect);

        // The case seen in ReconnectsStopsAfterSpecifiedTimes. A stopped server frees its UDP port at once (S51), so
        // the connect may be refused (ICMP port unreachable) before the timeout; it must never take longer.
        var clientConfig = (QuicClientConfig)CreateClientConfig(Type.Quic);
        clientConfig.ConnectionTimeout = ConnectionTimeoutMs;

        var sw = Stopwatch.StartNew();
        var exception = await Assert.ThrowsExactlyAsync<TransportException>(async () =>
            await QuicTransport.ConnectAsync(clientConfig, CancellationToken.None).AsTask().Timeout(10));
        sw.Stop();

        await Assert.That(exception!.Error).IsEqualTo(TransportError.ConnectionTimeout)
            .Or.IsEqualTo(TransportError.ConnectionRefused);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(ConnectionTimeoutMs) + Slack)
            .Because("The connect must fail within the connection timeout, not when msquic finishes the shutdown.");
    }

    private static async Task AssertConnectTimesOut(QuicClientConfig clientConfig)
    {
        clientConfig.ConnectionTimeout = ConnectionTimeoutMs;

        var sw = Stopwatch.StartNew();
        var exception = await Assert.ThrowsExactlyAsync<TransportException>(async () =>
            await QuicTransport.ConnectAsync(clientConfig, CancellationToken.None).AsTask().Timeout(10));
        sw.Stop();

        await Assert.That(exception!.Error).IsEqualTo(TransportError.ConnectionTimeout);
        await Assert.That(sw.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(ConnectionTimeoutMs - 50))
            .Because("The silent peer never answers, so only the timeout ends the connect.");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(ConnectionTimeoutMs) + Slack)
            .Because("The connect must fail within the connection timeout, not when msquic finishes the shutdown.");
    }
}
