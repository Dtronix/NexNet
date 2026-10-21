using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Frame and byte counters.
/// </summary>
internal class MetricsIoTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task FramesAndBytes_MatchBetweenPeers_AndSurviveDisconnect(Type type)
    {
        using var recorder = new MetricsRecorder();
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        serverConfig.MeterFactory = recorder;
        clientConfig.MeterFactory = recorder;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        server.OnNexusCreated = nexus => nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value);
        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        for (var i = 0; i < 10; i++)
            await client.Proxy.ServerTaskValueWithParam(i).Timeout(1);

        double Value(string instrument, string role, string direction) => recorder.ObserveSum(instrument,
            ("nexnet.role", role), ("nexnet.direction", direction));

        // Everything one side wrote, the other read: equal once nothing is in flight.
        await MetricsSessionTests.WaitFor(() =>
            Value("nexnet.io.bytes", "client", "sent") == Value("nexnet.io.bytes", "server", "received")
            && Value("nexnet.io.bytes", "server", "sent") == Value("nexnet.io.bytes", "client", "received")
            && Value("nexnet.io.frames", "client", "sent") == Value("nexnet.io.frames", "server", "received")
            && Value("nexnet.io.frames", "server", "sent") == Value("nexnet.io.frames", "client", "received")).Timeout(2);

        var clientFramesSent = Value("nexnet.io.frames", "client", "sent");
        var clientBytesSent = Value("nexnet.io.bytes", "client", "sent");
        Assert.That(clientFramesSent, Is.GreaterThanOrEqualTo(11)); // greeting + 10 invocations
        Assert.That(clientBytesSent, Is.GreaterThan(clientFramesSent * 3));

        // Retired sessions keep counting: the counters never go back.
        await client.DisconnectAsync().Timeout(1);
        await MetricsSessionTests.WaitFor(() => recorder.Metrics.TableSize.Count == 0).Timeout(2);
        Assert.That(Value("nexnet.io.frames", "client", "sent"), Is.GreaterThan(clientFramesSent));
        Assert.That(Value("nexnet.io.bytes", "client", "sent"), Is.GreaterThan(clientBytesSent));
    }
}
