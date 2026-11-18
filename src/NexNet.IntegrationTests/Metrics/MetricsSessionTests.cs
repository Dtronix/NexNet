using NexNet.Diagnostics;
using NexNet.IntegrationTests.Security;
using NexNet.Messages;
using NexNet.RateLimiting;
using NexNet.Transports;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Session instruments: active and handshaking sessions, disconnects, duration and rejected connections.
/// </summary>
internal class MetricsSessionTests : BaseTests
{
    private MetricsRecorder _recorder = null!;

    protected override void SetUp()
    {
        base.SetUp();
        _recorder = new MetricsRecorder();
    }

    protected override async Task TearDown()
    {
        _recorder.Dispose();
        await base.TearDown();
    }

    private (ServerConfig, ClientConfig) CreateConfigs(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        serverConfig.MeterFactory = _recorder;
        clientConfig.MeterFactory = _recorder;
        return (serverConfig, clientConfig);
    }

    private static string TransportName(Type type) => type switch
    {
        Type.Uds => "uds",
        Type.Tcp => "tcp",
        Type.TcpTls => "tls",
        Type.Quic => "quic",
        Type.WebSocket => "websocket",
        Type.HttpSocket => "httpsocket",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    [Test]
    [Transports(Except = new[] { Type.Quic })]
    public async Task ActiveSessions_CountedWhileConnected(Type type)
    {
        var (serverConfig, clientConfig) = CreateConfigs(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var transport = TransportName(type);
        await Assert.That(_recorder.ObserveSum("nexnet.session.active",
            ("nexnet.role", "server"), ("nexnet.transport", transport))).IsEqualTo(1);
        await Assert.That(_recorder.ObserveSum("nexnet.session.active",
            ("nexnet.role", "client"), ("nexnet.transport", transport))).IsEqualTo(1);
        await Assert.That(_recorder.ObserveSum("nexnet.session.handshaking")).IsEqualTo(0);

        await client.DisconnectAsync().Timeout(1);
        await WaitFor(() => _recorder.Metrics.TableSize.Count == 0).Timeout(2);

        await Assert.That(_recorder.ObserveSum("nexnet.session.active")).IsEqualTo(0);
    }

    [Test]
    public async Task Disconnect_RecordedOnceWithReasonPhaseAndDuration()
    {
        var (serverConfig, clientConfig) = CreateConfigs(Type.Uds);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await client.DisconnectAsync().Timeout(1);
        await WaitFor(() => _recorder.Events("nexnet.session.disconnects").Count == 2).Timeout(2);
        await Task.Delay(100);

        var disconnects = _recorder.Events("nexnet.session.disconnects");
        await Assert.That(disconnects).Count().IsEqualTo(2);
        await Assert.That(disconnects.Count(e => e.Has("nexnet.role", "client"))).IsEqualTo(1);
        await Assert.That(disconnects.Count(e => e.Has("nexnet.role", "server"))).IsEqualTo(1);
        await Assert.That(disconnects.All(e => e.Has("nexnet.disconnect.reason", "graceful"))).IsTrue();
        await Assert.That(disconnects.All(e => e.Has("nexnet.session.phase", "established"))).IsTrue();

        var durations = _recorder.Events("nexnet.session.duration");
        await Assert.That(durations).Count().IsEqualTo(2);
        await Assert.That(durations.All(e => e.Value > 0 && e.Has("nexnet.disconnect.reason", "graceful"))).IsTrue();
    }

    [Test]
    public async Task HandshakeTimeout_RecordedInHandshakePhase()
    {
        var serverConfig = CreateServerConfig(Type.Tcp);
        serverConfig.MeterFactory = _recorder;
        serverConfig.HandshakeTimeout = 200;
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        using var client = new RawTcpClient(serverConfig, false, CurrentTcpPort!.Value, Logger);
        await client.ConnectAsync();
        await client.SendProtocolHeaderAsync();
        await client.ReadProtocolHeaderAsync();

        await WaitFor(() => _recorder.ObserveSum("nexnet.session.handshaking") == 1).Timeout(1);
        await WaitFor(() => _recorder.Events("nexnet.session.disconnects").Count == 1).Timeout(2);

        var disconnect = _recorder.Events("nexnet.session.disconnects").Single();
        await Assert.That(disconnect.Has("nexnet.disconnect.reason", "timeout")).IsTrue();
        await Assert.That(disconnect.Has("nexnet.session.phase", "handshake")).IsTrue();
        await Assert.That(disconnect.Has("nexnet.role", "server")).IsTrue();
    }

    [Test]
    public async Task RateLimitedConnection_RecordedAsRejection()
    {
        var (serverConfig, clientConfig) = CreateConfigs(Type.Tcp);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        var (client1, _) = CreateClient(clientConfig);
        await client1.ConnectAsync().Timeout(1);

        var (client2, _) = CreateClient(clientConfig);
        _ = client2.TryConnectAsync();

        await WaitFor(() => _recorder.Events("nexnet.connection.rejections").Count == 1).Timeout(2);
        var rejection = _recorder.Events("nexnet.connection.rejections").Single();
        await Assert.That(rejection.Has("nexnet.rejection.reason", "max_concurrent_connections")).IsTrue();
        await Assert.That(rejection.Has("nexnet.transport", "tcp")).IsTrue();
    }

    [Test]
    public async Task ConnectDisconnectChurn_LeavesTableEmptyAndShrunk()
    {
        var (serverConfig, clientConfig) = CreateConfigs(Type.Uds);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        const int rounds = 5;
        const int clients = 20;
        for (var round = 0; round < rounds; round++)
        {
            var connected = new List<NexusClient<TestInterfaces.ClientNexus, TestInterfaces.ClientNexus.ServerProxy>>();
            for (var i = 0; i < clients; i++)
            {
                var (client, _) = CreateClient(clientConfig);
                await client.ConnectAsync().Timeout(1);
                connected.Add(client);
            }

            await Assert.That(_recorder.Metrics.TableSize.Count).IsEqualTo(clients * 2);

            foreach (var client in connected)
                await client.DisconnectAsync().Timeout(1);

            await WaitFor(() => _recorder.Metrics.TableSize.Count == 0).Timeout(2);
        }

        await Assert.That(_recorder.Metrics.TableSize.Capacity).IsEqualTo(16);
        await WaitFor(() => _recorder.Events("nexnet.session.disconnects").Count == rounds * clients * 2).Timeout(2);
    }

    [Test]
    public async Task SharedMeter_UsedWithoutFactory()
    {
        var shared = NexusMetrics.Get(null);
        await Assert.That(NexusMetrics.Get(null)).IsSameReferenceAs(shared);
        await Assert.That(shared.Meter.Name).IsEqualTo(NexusMeter.Name);
        await Assert.That(NexusMetrics.Get(_recorder)).IsNotSameReferenceAs(shared);
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        while (!condition())
            await Task.Delay(10);
    }
}