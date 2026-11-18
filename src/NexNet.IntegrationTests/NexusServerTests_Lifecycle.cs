using System.Diagnostics;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.RateLimiting;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Server start/stop lifecycle: bind failures, restart, and stop latency.
/// </summary>
internal class NexusServerTests_Lifecycle : BaseTests
{
    /// <summary>
    /// A bind failure must leave the server Stopped (not Running without a listener), and a later start, once the
    /// address is free, must work.
    /// </summary>
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task BindFailure_LeavesServerStopped_AndLaterStartWorks(Type type)
    {
        var config = CreateServerConfig(type);
        var occupier = CreateServer(config, null);
        var server = CreateServer(config, null);

        await occupier.StartAsync().Timeout(1);

        var exception = await Assert.ThrowsAsync<TransportException>(() => server.StartAsync().Timeout(1));
        Assert.That(exception!.Error, Is.EqualTo(TransportError.AddressInUse));
        Assert.That(exception.Message, Does.Contain($"'{config.TransportName}'"),
            "The exception should name the endpoint that failed to bind.");
        Assert.That(server.State, Is.EqualTo(NexusServerState.Stopped));

        await occupier.StopAsync().Timeout(1);

        await server.StartAsync().Timeout(1);
        Assert.That(server.State, Is.EqualTo(NexusServerState.Running));

        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [Test]
    public async Task NullListener_LeavesServerStopped()
    {
        var server = ServerNexus.CreateServer(new CustomServerConfig(ServerConnectionMode.Listener), () => null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());
        Assert.That(server.State, Is.EqualTo(NexusServerState.Stopped));
        await server.DisposeAsync();
    }

    /// <summary>
    /// The rate limiter is disposed on stop; a restarted server must create a new one and keep enforcing it.
    /// </summary>
    [TestCase(Type.Tcp)]
    [TestCase(Type.Uds)]
    public async Task RateLimiting_AppliesAfterRestart(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.RateLimiting = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
        var server = CreateServer(serverConfig, null);

        await server.StartAsync().Timeout(1);
        Assert.That(server.GetSnapshot().RateLimiter, Is.Not.Null);
        await server.StopAsync().Timeout(1);
        Assert.That(server.GetSnapshot().RateLimiter, Is.Null);

        await server.StartAsync().Timeout(1);
        Assert.That(server.GetSnapshot().RateLimiter, Is.Not.Null);

        var (client1, _) = CreateClient(CreateClientConfig(type));
        await client1.ConnectAsync().Timeout(1);
        Assert.That(client1.State, Is.EqualTo(ConnectionState.Connected));

        var (client2, _) = CreateClient(CreateClientConfig(type));
        var result = await client2.TryConnectAsync().Timeout(2);
        Assert.That(result.Success, Is.False, "The second connection should be rejected by the rate limiter.");
        Assert.That(client2.State, Is.Not.EqualTo(ConnectionState.Connected));
    }

    /// <summary>
    /// Stop disconnects sessions in parallel, so each session's disconnect delay overlaps instead of adding up
    /// (200 sessions x 200 ms would take 40 s sequentially).
    /// </summary>
    [Test]
    public async Task Stop_With200Sessions_CompletesQuickly()
    {
        const int sessionCount = 200;
        var serverConfig = CreateServerConfig(Type.Tcp, BasePipeTests.LogMode.None);
        serverConfig.DisconnectDelay = 200;
        serverConfig.MaxPendingHandshakes = 1000;

        var connected = 0;
        var allConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnConnectedEvent = _ =>
        {
            if (Interlocked.Increment(ref connected) == sessionCount)
                allConnected.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await server.StartAsync().Timeout(1);

        var clients = new List<NexusClient<ClientNexus, ClientNexus.ServerProxy>>(sessionCount);
        for (var i = 0; i < sessionCount; i++)
            clients.Add(CreateClient(CreateClientConfig(Type.Tcp, BasePipeTests.LogMode.None)).client);

        foreach (var batch in clients.Chunk(25))
            await Task.WhenAll(batch.Select(c => c.ConnectAsync())).Timeout(10);

        await allConnected.Task.Timeout(10);

        var stopwatch = Stopwatch.StartNew();
        await server.StopAsync().Timeout(10);
        stopwatch.Stop();

        Assert.That(server.State, Is.EqualTo(NexusServerState.Stopped));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));

        await Task.WhenAll(clients.Select(c => c.DisconnectedTask)).Timeout(5);
    }

    [TestCase(Type.Tcp)]
    public async Task DisposeAsync_WhileRunning_StopsAndDisposes(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        await server.DisposeAsync().AsTask().Timeout(2);

        Assert.That(server.State, Is.EqualTo(NexusServerState.Disposed));
        await client.DisconnectedTask.Timeout(2);
        await server.StoppedTask!.Timeout(1);
    }
}
