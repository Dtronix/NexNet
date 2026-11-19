using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using NexNet.Diagnostics;
using NexNet.IntegrationTests.Metrics;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// [NexusMethod(SeparatePool = true)]: methods that run in a separate pool of MaxSeparatePoolInvocations slots with
/// its own queue, never taking or waiting for one of the MaxConcurrentConnectionInvocations slots.
/// </summary>
internal class SeparatePoolTests : BaseTests
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

    /// <summary>
    /// Gates by invocation id: a gated method signals Started[id] and waits for Release[id].
    /// </summary>
    private sealed class Gates
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _released = new();
        public readonly ConcurrentQueue<string> Log = new();

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started(int id) => _started.GetOrAdd(id, _ => New()).Task;
        public void Release(int id) => _released.GetOrAdd(id, _ => New()).TrySetResult();
        public bool HasStarted(int id) => _started.TryGetValue(id, out var s) && s.Task.IsCompleted;

        public async ValueTask Run(string name, int id)
        {
            Log.Enqueue($"{name}{id} start");
            _started.GetOrAdd(id, _ => New()).TrySetResult();
            await _released.GetOrAdd(id, _ => New()).Task;
            Log.Enqueue($"{name}{id} end");
        }

        public void ReleaseAll()
        {
            for (var i = 0; i < 100; i++)
                Release(i);
        }
    }

    private async Task<(NexusServer<SeparatePoolServerNexus, SeparatePoolServerNexus.ClientProxy> Server,
        NexusClient<SeparatePoolClientNexus, SeparatePoolClientNexus.ServerProxy> Client)> Start(
        Action<ServerConfig> configure, Action<SeparatePoolServerNexus> nexusCreated)
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.MeterFactory = _recorder;
        configure(serverConfig);
        var server = CreateServer<SeparatePoolServerNexus, SeparatePoolServerNexus.ClientProxy>(serverConfig, nexusCreated);
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        var (client, _) = CreateClient<SeparatePoolClientNexus, SeparatePoolClientNexus.ServerProxy>(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
        return (server, client);
    }

    private static NexusInvocationSnapshot? Invocations(
        NexusServer<SeparatePoolServerNexus, SeparatePoolServerNexus.ClientProxy> server)
        => server.GetSnapshot().Sessions.SingleOrDefault()?.Invocations;

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                Fail.Test($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task MaxSeparatePoolInvocations_DefaultsFollowSerializerSecurity_ExplicitWins_Range()
    {
        var untrusted = CreateServerConfig(Type.Uds);
        await Assert.That(untrusted.MaxSeparatePoolInvocations).IsEqualTo(4);

        var trusted = CreateServerConfig(Type.Uds);
        trusted.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Trusted;
        await Assert.That(trusted.MaxSeparatePoolInvocations).IsEqualTo(16);

        // An explicit value wins, whichever is set first.
        var explicitUntrusted = CreateServerConfig(Type.Uds);
        explicitUntrusted.MaxSeparatePoolInvocations = 7;
        await Assert.That(explicitUntrusted.MaxSeparatePoolInvocations).IsEqualTo(7);

        var explicitTrusted = CreateServerConfig(Type.Uds);
        explicitTrusted.MaxSeparatePoolInvocations = 3;
        explicitTrusted.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Trusted;
        await Assert.That(explicitTrusted.MaxSeparatePoolInvocations).IsEqualTo(3);

        // 1 to 1000.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => trusted.MaxSeparatePoolInvocations = 0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => trusted.MaxSeparatePoolInvocations = 1001);
        trusted.MaxSeparatePoolInvocations = 1;
        await Assert.That(trusted.MaxSeparatePoolInvocations).IsEqualTo(1);
        trusted.MaxSeparatePoolInvocations = 1000;
        await Assert.That(trusted.MaxSeparatePoolInvocations).IsEqualTo(1000);
    }

    [Test]
    public async Task SeparatePoolCall_TakesNoNormalSlot_PlainCallRunsWhileItRuns()
    {
        var gates = new Gates();
        var (server, client) = await Start(
            config => config.MaxConcurrentConnectionInvocations = 1,
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.PlainHandler = id => gates.Run("plain", id);
            });

        var slow = client.Proxy.Slow(1).AsTask();
        await gates.Started(1).Timeout(1);

        // The only normal slot is free: the plain call runs and returns while the slow one still runs.
        gates.Release(2);
        await client.Proxy.Plain(2).Timeout(1);
        await Assert.That(slow.IsCompleted).IsFalse();

        var invocations = Invocations(server)!;
        await Assert.That(invocations.SeparatePoolExecuting).IsEqualTo(1);
        await Assert.That(invocations.Executing).IsEqualTo(0);

        gates.Release(1);
        await slow.Timeout(1);
    }

    [Test]
    public async Task FullSeparatePool_QueuesSeparateCalls_NeverBlocksPlainCalls()
    {
        var gates = new Gates();
        var (server, client) = await Start(
            config =>
            {
                config.MaxConcurrentConnectionInvocations = 1;
                config.MaxSeparatePoolInvocations = 1;
            },
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.PlainHandler = id => gates.Run("plain", id);
            });

        var slow1 = client.Proxy.Slow(1).AsTask();
        await gates.Started(1).Timeout(1);
        var slow2 = client.Proxy.Slow(2).AsTask();
        await WaitFor(() => Invocations(server)?.SeparatePoolQueued == 1, "the second separate-pool call queued");

        // Sent after the queued separate-pool call, the plain call runs and completes first.
        gates.Release(3);
        await client.Proxy.Plain(3).Timeout(1);
        await Assert.That(slow1.IsCompleted).IsFalse();
        await Assert.That(slow2.IsCompleted).IsFalse();
        await Assert.That(gates.HasStarted(2)).IsFalse().Because("The second separate-pool call started while the pool was full.");

        // The second starts only once the first has finished.
        gates.Release(1);
        await slow1.Timeout(1);
        await gates.Started(2).Timeout(1);
        gates.Release(2);
        await slow2.Timeout(1);

        await Assert.That(gates.Log).IsEquivalentTo(new[]
        {
            "slow1 start",
            "plain3 start",
            "plain3 end",
            "slow1 end",
            "slow2 start",
            "slow2 end",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Snapshot_ReportsSeparatePoolQueuedExecutingAndMax()
    {
        var gates = new Gates();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (server, client) = await Start(
            config =>
            {
                UseFakeTime(config, time);
                config.MaxConcurrentConnectionInvocations = 1;
                config.MaxSeparatePoolInvocations = 1;
            },
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.PlainHandler = id => gates.Run("plain", id);
            });

        var calls = new List<Task>
        {
            client.Proxy.Slow(1).AsTask(),
            client.Proxy.Slow(2).AsTask(),
            client.Proxy.Plain(3).AsTask(),
            client.Proxy.Plain(4).AsTask(),
        };

        await WaitFor(() => Invocations(server) is { SeparatePoolExecuting: 1, SeparatePoolQueued: 1, Executing: 1, Queued: 1 },
            "one running and one queued in each pool");
        time.Advance(TimeSpan.FromSeconds(3));

        var invocations = Invocations(server)!;
        await Assert.That(invocations.SeparatePoolMaxExecuting).IsEqualTo(1);
        await Assert.That(invocations.MaxExecuting).IsEqualTo(1);
        await Assert.That(invocations.SeparatePoolOldestQueuedWait).IsEqualTo(TimeSpan.FromSeconds(3));

        gates.ReleaseAll();
        await Task.WhenAll(calls).Timeout(2);
        await WaitFor(() => Invocations(server) is { SeparatePoolExecuting: 0, SeparatePoolQueued: 0, Executing: 0, Queued: 0 },
            "both pools idle");
        await Assert.That(Invocations(server)!.SeparatePoolOldestQueuedWait).IsNull();
    }

    [Test]
    public async Task QueueWarning_SeparatePool_NamesMaxSeparatePoolInvocations_SharesRateLimit()
    {
        var gates = new Gates();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger();
        var (server, client) = await Start(
            config =>
            {
                UseFakeTime(config, time);
                config.Logger = logger;
                config.InvocationQueueWarningThreshold = 1000;
                config.MaxConcurrentConnectionInvocations = 1;
                config.MaxSeparatePoolInvocations = 1;
            },
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.PlainHandler = id => gates.Run("plain", id);
            });
        string[] Warnings() => logger.Warnings.Where(w => w.Contains("execution slot")).ToArray();

        var calls = new List<Task> { client.Proxy.Slow(1).AsTask(), client.Proxy.Plain(3).AsTask() };
        await gates.Started(1).Timeout(1);
        await gates.Started(3).Timeout(1);
        calls.Add(client.Proxy.Slow(2).AsTask());
        calls.Add(client.Proxy.Plain(4).AsTask());
        await WaitFor(() => Invocations(server) is { SeparatePoolQueued: 1, Queued: 1 }, "one queued in each pool");

        // Both queued calls wait 2 s, over the 1 s threshold. The separate-pool one is reported first.
        time.Advance(TimeSpan.FromSeconds(2));
        gates.Release(1);
        await gates.Started(2).Timeout(1);
        await WaitFor(() => Warnings().Length >= 1, "the separate-pool warning");
        await Assert.That(Warnings()).Count().IsEqualTo(1);
        await Assert.That(Warnings()[0]).Contains("(MaxSeparatePoolInvocations 1, threshold 1000 ms).");
        await Assert.That(Warnings()[0]).DoesNotContain("MaxConcurrentConnectionInvocations");

        // The normal-pool wait falls in the same 30 s window: suppressed.
        gates.Release(3);
        await gates.Started(4).Timeout(1);
        await Task.Delay(100);
        await Assert.That(Warnings()).Count().IsEqualTo(1).Because("The pools do not share the 30 s warning window.");

        // 31 s later the next slow separate-pool call is reported, counting the suppressed normal-pool one.
        calls.Add(client.Proxy.Slow(5).AsTask());
        await WaitFor(() => Invocations(server)?.SeparatePoolQueued == 1, "the third separate-pool call queued");
        time.Advance(TimeSpan.FromSeconds(31));
        gates.Release(2);
        await gates.Started(5).Timeout(1);
        await WaitFor(() => Warnings().Length >= 2, "the second warning");
        await Assert.That(Warnings()).Count().IsEqualTo(2);
        await Assert.That(Warnings()[1]).Contains("MaxSeparatePoolInvocations 1");
        await Assert.That(Warnings()[1]).EndsWith("(1 more since the last warning).");

        gates.ReleaseAll();
        await Task.WhenAll(calls).Timeout(2);
    }

    /// <summary>
    /// Runs the server on <paramref name="time"/>; the clock is advanced past the timeouts, so they are raised.
    /// </summary>
    private static void UseFakeTime(ServerConfig config, FakeTimeProvider time)
    {
        config.Time = time;
        config.Timeout = 300_000;
        config.ProgressTimeout = 600_000;
    }

    [Test]
    public async Task Metrics_QueuedExecutingAndQueueWait_CarryThePoolTag()
    {
        var gates = new Gates();
        var (server, client) = await Start(
            config =>
            {
                config.MaxConcurrentConnectionInvocations = 1;
                config.MaxSeparatePoolInvocations = 1;
            },
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.PlainHandler = id => gates.Run("plain", id);
            });

        double Sum(string instrument, string pool)
            => _recorder.ObserveSum(instrument, ("nexnet.role", "server"), ("nexnet.invocation.pool", pool));

        var calls = new List<Task>
        {
            client.Proxy.Slow(1).AsTask(),
            client.Proxy.Slow(2).AsTask(),
            client.Proxy.Slow(3).AsTask(),
            client.Proxy.Plain(4).AsTask(),
        };

        await WaitFor(() => Sum("nexnet.invocation.executing", "separate") == 1
                            && Sum("nexnet.invocation.queued", "separate") == 2
                            && Sum("nexnet.invocation.executing", "normal") == 1
                            && Sum("nexnet.invocation.queued", "normal") == 0,
            "executing/queued observed per pool");

        gates.ReleaseAll();
        await Task.WhenAll(calls).Timeout(2);

        await WaitFor(() => _recorder.Events("nexnet.invocation.queue_wait").Count >= 4, "four queue waits recorded");
        var waits = _recorder.Events("nexnet.invocation.queue_wait");
        await Assert.That(waits.Count(w => w.Has("nexnet.invocation.pool", "separate"))).IsEqualTo(3);
        await Assert.That(waits.Count(w => w.Has("nexnet.invocation.pool", "normal"))).IsEqualTo(1);
        await Assert.That(waits.All(w => w.Has("nexnet.role", "server") && w.Has("nexnet.transport", "uds"))).IsTrue();
    }

    [Test]
    public async Task BothQueues_BoundedByInvocationCredits_CreditReturnedWhenSeparateCallDequeued()
    {
        var gates = new Gates();
        var (server, client) = await Start(
            config =>
            {
                config.MaxConcurrentConnectionInvocations = 1;
                config.MaxSeparatePoolInvocations = 1;
                config.InvocationCredits = 2;
            },
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.PlainHandler = id => gates.Run("plain", id);
            });

        var calls = new List<Task>
        {
            // Both run: dequeued at once, their credit returned.
            client.Proxy.Slow(1).AsTask(),
            client.Proxy.Plain(2).AsTask(),
        };
        await WaitFor(() => Invocations(server) is { SeparatePoolExecuting: 1, Executing: 1 }, "one running in each pool");

        // One queued in each pool: both credits in use.
        calls.Add(client.Proxy.Slow(3).AsTask());
        calls.Add(client.Proxy.Plain(4).AsTask());
        await WaitFor(() => Invocations(server) is { SeparatePoolQueued: 1, Queued: 1 }, "one queued in each pool");

        // No credit left: the next call waits on the client.
        calls.Add(client.Proxy.Plain(5).AsTask());
        await WaitFor(() => client.GetSnapshot()!.Invocations.CreditWaits == 1, "the fifth call waiting for credit");
        await Task.Delay(100);
        await Assert.That(Invocations(server)!.Queued).IsEqualTo(1).Because("A call was queued beyond InvocationCredits.");

        // The queued separate-pool call is dequeued when the first finishes; its credit lets the fifth call in.
        gates.Release(1);
        await WaitFor(() => Invocations(server) is { SeparatePoolQueued: 0, SeparatePoolExecuting: 1, Queued: 2 },
            "the separate-pool call dequeued and the fifth call queued");
        await WaitFor(() => client.GetSnapshot()!.Invocations.CreditWaits == 0, "no call waiting for credit");

        gates.ReleaseAll();
        await Task.WhenAll(calls).Timeout(2);
    }

    [Test]
    public async Task Cancellation_ReachesRunningSeparatePoolMethod()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client) = await Start(
            config => config.MaxConcurrentConnectionInvocations = 1,
            nexus => nexus.SlowWithCancellationHandler = async (_, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    observed.TrySetResult();
                    throw;
                }
            });

        using var cts = new CancellationTokenSource();
        var call = client.Proxy.SlowWithCancellation(1, cts.Token).AsTask();
        await started.Task.Timeout(1);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await call.Timeout(1));
        await observed.Task.Timeout(1);
        await WaitFor(() => Invocations(server) is { SeparatePoolExecuting: 0 }, "the separate pool idle");
    }

    [Test]
    public async Task Cancellation_ReachesQueuedSeparatePoolMethod()
    {
        var gates = new Gates();
        var sawCanceledToken = new ConcurrentQueue<bool>();
        var (server, client) = await Start(
            config => config.MaxSeparatePoolInvocations = 1,
            nexus =>
            {
                nexus.SlowHandler = id => gates.Run("slow", id);
                nexus.SlowWithCancellationHandler = async (_, token) =>
                {
                    sawCanceledToken.Enqueue(token.IsCancellationRequested);
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                };
            });

        var slow = client.Proxy.Slow(1).AsTask();
        await gates.Started(1).Timeout(1);

        using var cts = new CancellationTokenSource();
        var queued = client.Proxy.SlowWithCancellation(2, cts.Token).AsTask();
        await WaitFor(() => Invocations(server)?.SeparatePoolQueued == 1, "the cancelable call queued");

        var framesSentBeforeCancel = client.GetSnapshot()!.FramesSent;
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued.Timeout(1));

        // A canceled call returns at once, and the client's control sender sends the cancellation afterwards, on the
        // thread pool. Releasing the slot before the server has read it lets the queued method start with a
        // token that is not canceled yet (it is canceled a moment later). That is a race in the test, not the
        // product, so wait for the server to read it: wait until the cancellation frame is written, then make a
        // round trip on the same connection. The server reads frames in order, so the reply proves it has
        // handled the cancellation.
        await WaitFor(() => client.GetSnapshot()!.FramesSent > framesSentBeforeCancel, "the cancellation sent");
        await client.Proxy.Plain(0).Timeout(TestTimeouts.Connect);

        // Once a slot frees, the canceled call either does not run or runs with its token already canceled, and ends.
        gates.Release(1);
        await slow.Timeout(1);
        await WaitFor(() => Invocations(server) is { SeparatePoolExecuting: 0, SeparatePoolQueued: 0 },
            "the canceled call ended");
        await Assert.That(sawCanceledToken.All(canceled => canceled)).IsTrue().Because("The queued separate-pool method started with a token that was not canceled.");
    }

    [Test]
    public async Task SeparatePoolMethod_RunsOnThreadPoolThread()
    {
        var (_, client) = await Start(_ => { }, _ => { });

        await Assert.That(await client.Proxy.SlowIsOnThreadPool().Timeout(1)).IsTrue();
    }
}