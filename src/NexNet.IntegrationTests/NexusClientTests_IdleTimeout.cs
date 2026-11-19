using NexNet.IntegrationTests.Metrics;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The client's ping interval derived from the idle timeout the server advertises in its greeting.
/// </summary>
internal partial class NexusClientTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp, Type.WebSocket)]
    public async Task PingInterval_DerivedFromServerIdleTimeout(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(clientConfig.PingInterval).IsNull();
        await Assert.That(client.GetSnapshot()!.PingInterval).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task PingInterval_DerivedIntervalIsAtLeastOneSecond(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 1500;
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(client.GetSnapshot()!.PingInterval).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task PingInterval_DerivedInterval_DrivesThePingTimer(Type type)
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        clientConfig.Time = fakeTime;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        var pingCount = 0;
        var pinged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        ServerTaps.OnReceive(serverConfig, (_, frame) =>
        {
            if (frame.Type == MessageType.Ping && Interlocked.Increment(ref pingCount) == 1)
                pinged.TrySetResult();
        });
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        fakeTime.Advance(TimeSpan.FromMilliseconds(1999));
        await Barrier(client.Proxy);
        await Assert.That(Volatile.Read(ref pingCount)).IsEqualTo(0).Because("No ping before a third of the server's idle timeout.");

        fakeTime.Advance(TimeSpan.FromMilliseconds(1));
        await pinged.Task.Timeout(T(1));
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task PingInterval_ExplicitIntervalIsRespected(Type type)
    {
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        clientConfig.Logger = logger;
        clientConfig.PingInterval = 500;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(client.GetSnapshot()!.PingInterval).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(logger.Warnings.Any(w => w.Contains("PingInterval"))).IsFalse();
    }

    [Test]
    [Transports(Type.Uds)]
    public async Task PingInterval_ExplicitIntervalNotBelowServerIdleTimeout_LogsWarning(Type type)
    {
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        clientConfig.Logger = logger;
        clientConfig.PingInterval = 6000;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(client.GetSnapshot()!.PingInterval).IsEqualTo(TimeSpan.FromMilliseconds(6000));
        await Assert.That(logger.Warnings.Any(w => w.Contains("PingInterval (6000 ms)") && w.Contains("(6000 ms)"))).IsTrue();
    }

    [Test]
    public async Task PingInterval_RejectsZeroAndNegative()
    {
        var config = CreateClientConfig(Type.Uds);
        await Assert.That(() => config.PingInterval = 0).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => config.PingInterval = -1).ThrowsExactly<ArgumentOutOfRangeException>();
        config.PingInterval = null;
        await Assert.That(config.PingInterval).IsNull();
    }

    [Test]
    public async Task PingInterval_WithoutAdvertisedTimeout_DefaultsToTenSeconds()
    {
        // A server that predates the idle timeout field advertises 0.
        var config = CreateClientConfig(Type.Uds);
        await Assert.That(config.GetEffectivePingInterval(0)).IsEqualTo(10_000);
        await Assert.That(config.GetEffectivePingInterval(30_000)).IsEqualTo(10_000);
        await Assert.That(config.GetEffectivePingInterval(300_000)).IsEqualTo(100_000);
        config.PingInterval = 250;
        await Assert.That(config.GetEffectivePingInterval(0)).IsEqualTo(250);
    }
}
