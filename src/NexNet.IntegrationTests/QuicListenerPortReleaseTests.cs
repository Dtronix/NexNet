using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

#pragma warning disable VSTHRD200
#pragma warning disable CA1416

namespace NexNet.IntegrationTests;

/// <summary>
/// A stopped QUIC server frees its UDP port at once. msquic keeps a UDP binding alive while any connection on it still
/// has an open handle, and the server's QUIC transport used to close its connections without disposing them, so the
/// port stayed bound (exclusively) until the finalizers ran: about 16 s after a stop, more than 60 s after a second one.
/// </summary>
[NotInParallel]
internal class QuicListenerPortReleaseTests : BaseTests
{
    // Freed at once after the fix; this allows for a loaded machine.
    private static readonly TimeSpan RebindTimeout = TimeSpan.FromSeconds(3);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task Quic_StoppedServer_FreesItsUdpPort(int clients)
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Skip.Test("QUIC (msquic) is not available on this platform.");

        var serverConfig = CreateServerConfig(Type.Quic);
        var port = ((NexNet.Quic.QuicServerConfig)serverConfig).EndPoint;
        var server = CreateServer(serverConfig, null);

        // Twice: a second stop of the same server held the port for more than a minute.
        for (var round = 0; round < 2; round++)
        {
            await server.StartAsync().Timeout(TestTimeouts.Connect);
            for (var i = 0; i < clients; i++)
            {
                var (client, _) = CreateClient(CreateClientConfig(Type.Quic));
                await client.ConnectAsync().Timeout(TestTimeouts.Connect);
            }

            await server.StopAsync().Timeout(TestTimeouts.Connect);

            var freed = await WaitForUdpBindAsync(port, RebindTimeout);
            await Assert.That(freed).IsTrue()
                .Because($"Round {round}: the stopped server's UDP port {port} must be free within {RebindTimeout.TotalSeconds} s.");
        }
    }

    /// <summary>
    /// Binds the port exclusively, as msquic does, until it succeeds; false when it is still bound after the timeout.
    /// </summary>
    private static async Task<bool> WaitForUdpBindAsync(IPEndPoint endPoint, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            using (var socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                if (OperatingSystem.IsWindows())
                    socket.ExclusiveAddressUse = true;
                try
                {
                    socket.Bind(endPoint);
                    return true;
                }
                catch (SocketException)
                {
                    // Still bound.
                }
            }

            if (sw.Elapsed >= timeout)
                return false;
            await Task.Delay(20);
        }
    }
}
