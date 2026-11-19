using NexNet.IntegrationTests.Metrics;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The client's ping interval derived from the idle timeout the server advertises in its greeting.
/// </summary>
internal partial class NexusClientTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task PingInterval_DerivedFromServerIdleTimeout(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.That(clientConfig.PingInterval, Is.Null);
        Assert.That(client.GetSnapshot()!.PingInterval, Is.EqualTo(TimeSpan.FromSeconds(2)));
    }

    [TestCase(Type.Uds)]
    public async Task PingInterval_DerivedIntervalIsAtLeastOneSecond(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 1500;
        var clientConfig = CreateClientConfig(type);
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.That(client.GetSnapshot()!.PingInterval, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [TestCase(Type.Uds)]
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
        await server.StartAsync().Timeout(1);
        FireOnSend(clientConfig, (_, bytes) =>
        {
            if (bytes.Length == 1 && bytes[0] == (int)MessageType.Ping && Interlocked.Increment(ref pingCount) == 1)
                pinged.TrySetResult();
        });
        await client.ConnectAsync().Timeout(1);

        fakeTime.Advance(TimeSpan.FromMilliseconds(1999));
        await Task.Delay(50);
        Assert.That(Volatile.Read(ref pingCount), Is.EqualTo(0), "No ping before a third of the server's idle timeout.");

        fakeTime.Advance(TimeSpan.FromMilliseconds(1));
        await pinged.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    public async Task PingInterval_ExplicitIntervalIsRespected(Type type)
    {
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        clientConfig.Logger = logger;
        clientConfig.PingInterval = 500;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.That(client.GetSnapshot()!.PingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        Assert.That(logger.Warnings.Any(w => w.Contains("PingInterval")), Is.False);
    }

    [TestCase(Type.Uds)]
    public async Task PingInterval_ExplicitIntervalNotBelowServerIdleTimeout_LogsWarning(Type type)
    {
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(type);
        serverConfig.Timeout = 6000;
        var clientConfig = CreateClientConfig(type);
        clientConfig.Logger = logger;
        clientConfig.PingInterval = 6000;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        Assert.That(client.GetSnapshot()!.PingInterval, Is.EqualTo(TimeSpan.FromMilliseconds(6000)));
        Assert.That(logger.Warnings.Any(w => w.Contains("PingInterval (6000 ms)") && w.Contains("(6000 ms)")), Is.True);
    }

    [Test]
    public void PingInterval_RejectsZeroAndNegative()
    {
        var config = CreateClientConfig(Type.Uds);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.PingInterval = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => config.PingInterval = -1);
        config.PingInterval = null;
        Assert.That(config.PingInterval, Is.Null);
    }

    [Test]
    public void PingInterval_WithoutAdvertisedTimeout_DefaultsToTenSeconds()
    {
        // A server that predates the idle timeout field advertises 0.
        var config = CreateClientConfig(Type.Uds);
        Assert.That(config.GetEffectivePingInterval(0), Is.EqualTo(10_000));
        Assert.That(config.GetEffectivePingInterval(30_000), Is.EqualTo(10_000));
        Assert.That(config.GetEffectivePingInterval(300_000), Is.EqualTo(100_000));
        config.PingInterval = 250;
        Assert.That(config.GetEffectivePingInterval(0), Is.EqualTo(250));
    }
}
