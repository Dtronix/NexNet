using NexNet.IntegrationTests.Metrics;
using NexNet.Interop;
using NexNet.InteropServer;
using NexNet.Messages;
using NexNet.TestNexus.Interop;
using TestServer = NexNet.InteropServer.InteropServer<NexNet.TestNexus.Interop.InteropServerNexus, NexNet.TestNexus.Interop.InteropServerNexus.ClientProxy>;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The client's ping interval derived from the idle timeout the server advertises in its greeting.
/// </summary>
/// <remarks>
/// The matrix tests observe the interval on the wire: the client runs on a test clock and the server counts the pings it
/// receives. The tests left on the C# client read its log (PingInterval warning) or its configuration API.
/// </remarks>
internal partial class NexusClientTests
{
    /// <summary>
    /// Starts a server whose sessions time out after <paramref name="serverTimeoutMs"/> and connects the client of
    /// <paramref name="target"/> on a test clock. <paramref name="pings"/> counts the pings the server receives.
    /// </summary>
    private async Task<(TestServer Server, IClientUnderTest Client)> ConnectForPingAsync(
        ClientTarget target,
        int serverTimeoutMs,
        PingCounter pings,
        int? pingIntervalMs = null)
    {
        var server = await StartServer(new InteropServerOptions
        {
            Timeout = serverTimeoutMs,
            ConfigureEndpoint = (_, config) => ServerTaps.OnReceive(config, (_, frame) =>
            {
                if (frame.Type == MessageType.Ping)
                    pings.Increment();
            }),
        });
        try
        {
            var client = await Connect(target, server, spec =>
            {
                spec.FakeTime = true;
                spec.PingIntervalMs = pingIntervalMs;
            });
            return (server, client);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Asserts that the client sends its first ping after exactly <paramref name="intervalMs"/> of its test clock: none
    /// one millisecond before, one at the interval.
    /// </summary>
    private static async Task AssertFirstPingAfter(IClientUnderTest client, PingCounter pings, int intervalMs)
    {
        // A round trip: the server has received everything the client sent before it.
        await client.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));
        await Assert.That(pings.Count).IsEqualTo(0).Because("No ping before the interval starts.");

        await client.AdvanceTimeAsync(TimeSpan.FromMilliseconds(intervalMs - 1));
        await client.BarrierAsync().Timeout(T(1));
        await client.Proxy<IInteropServerNexus>().Ping().Timeout(T(1));
        await Assert.That(pings.Count).IsEqualTo(0).Because($"No ping before {intervalMs} ms.");

        await client.AdvanceTimeAsync(TimeSpan.FromMilliseconds(1));
        await pings.First.Timeout(T(1));
    }

    /// <summary>Counts the pings a server receives.</summary>
    private sealed class PingCounter
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task First => _first.Task;

        public void Increment()
        {
            Interlocked.Increment(ref _count);
            _first.TrySetResult();
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task PingInterval_DerivedFromServerIdleTimeout(ClientTarget target)
    {
        // A third of the server's idle timeout.
        var pings = new PingCounter();
        var (server, client) = await ConnectForPingAsync(target, 9000, pings);
        await using (server)
        await using (client)
        {
            await AssertFirstPingAfter(client, pings, 3000);
            await MatrixTestBase.EndAsync(client);
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task PingInterval_DerivedIntervalIsAtLeastOneSecond(ClientTarget target)
    {
        // A third of 1500 ms is 500 ms; the client pings no more often than once a second. The test's invocations keep
        // the session from idling out on the server's (real) clock.
        var pings = new PingCounter();
        var (server, client) = await ConnectForPingAsync(target, 1500, pings);
        await using (server)
        await using (client)
        {
            await AssertFirstPingAfter(client, pings, 1000);
            await MatrixTestBase.EndAsync(client);
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task PingInterval_DerivedInterval_DrivesThePingTimer(ClientTarget target)
    {
        var pings = new PingCounter();
        var (server, client) = await ConnectForPingAsync(target, 6000, pings);
        await using (server)
        await using (client)
        {
            await AssertFirstPingAfter(client, pings, 2000);

            // And again one interval later.
            await client.AdvanceTimeAsync(TimeSpan.FromMilliseconds(2000));
            var deadline = DateTime.UtcNow.AddSeconds(T(1));
            while (pings.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            await Assert.That(pings.Count).IsEqualTo(2);
            await MatrixTestBase.EndAsync(client);
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task PingInterval_ExplicitIntervalIsRespected(ClientTarget target)
    {
        // An explicit interval below the server's idle timeout wins over the derived one (2000 ms).
        var pings = new PingCounter();
        var (server, client) = await ConnectForPingAsync(target, 6000, pings, pingIntervalMs: 500);
        await using (server)
        await using (client)
        {
            await AssertFirstPingAfter(client, pings, 500);
            await MatrixTestBase.EndAsync(client);
        }
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
