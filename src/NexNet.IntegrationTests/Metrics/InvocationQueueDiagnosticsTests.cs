using Microsoft.Extensions.Time.Testing;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Time invocations wait for an execution slot (histogram, snapshot, warning) and time pipe writers wait for credit.
/// </summary>
internal class InvocationQueueDiagnosticsTests : BaseTests
{
    private MetricsRecorder _recorder = null!;

    public override void SetUp()
    {
        base.SetUp();
        _recorder = new MetricsRecorder();
    }

    public override void TearDown()
    {
        _recorder.Dispose();
        base.TearDown();
    }

    /// <summary>
    /// A server with one execution slot on a fake clock whose invocations of ServerTask each wait for their own gate,
    /// in the order they start.
    /// </summary>
    private async Task<(FakeTimeProvider Time, NexNet.NexusServer<TestInterfaces.ServerNexus, TestInterfaces.ServerNexus.ClientProxy> Server,
        NexNet.NexusClient<TestInterfaces.ClientNexus, TestInterfaces.ClientNexus.ServerProxy> Client, TaskCompletionSource[] Gates)>
        CreateOneSlotServer(ServerConfig serverConfig, int gates)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        serverConfig.Time = time;
        serverConfig.MeterFactory = _recorder;
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        // The fake clock is advanced past these; nothing may time out because of it.
        serverConfig.Timeout = 300_000;
        serverConfig.ProgressTimeout = 600_000;

        var released = Enumerable.Range(0, gates)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var started = -1;
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskEvent = async _ =>
        {
            var index = Interlocked.Increment(ref started);
            await released[index].Task;
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(1);
        return (time, server, client, released);
    }

    private static Task WaitForQueue(NexNet.NexusServer<TestInterfaces.ServerNexus, TestInterfaces.ServerNexus.ClientProxy> server,
        int executing, int queued)
        => MetricsSessionTests.WaitFor(() =>
        {
            var invocations = server.GetSnapshot().Sessions.SingleOrDefault()?.Invocations;
            return invocations != null && invocations.Executing == executing && invocations.Queued == queued;
        }).Timeout(2);

    /// <summary>
    /// Waits up to 2 s for a condition; the assertion that follows reports it if it never held.
    /// </summary>
    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(10);
    }

    [Test]
    public async Task QueueWait_RecordedForEveryInvocationFromQueuedToSlot()
    {
        var (time, server, client, gates) = await CreateOneSlotServer(CreateServerConfig(Type.Uds), 2);

        var first = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 0);
        var second = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 1);

        // The second waits 2 s on the server's clock for the slot the first holds.
        time.Advance(TimeSpan.FromSeconds(2));
        gates[0].SetResult();
        await first.Timeout(1);
        await WaitForQueue(server, executing: 1, queued: 0);
        gates[1].SetResult();
        await second.Timeout(1);

        await Eventually(() => _recorder.Events("nexnet.invocation.queue_wait").Count >= 2);
        var waits = _recorder.Events("nexnet.invocation.queue_wait");
        Assert.That(waits.Select(w => w.Value), Is.EqualTo(new[] { 0.0, 2.0 }));
        Assert.That(waits.All(w => w.Has("nexnet.role", "server") && w.Has("nexnet.transport", "uds")), Is.True);
    }

    [Test]
    public async Task Snapshot_OldestQueuedWait_ShowsHowLongTheOldestQueuedInvocationWaited()
    {
        var (time, server, client, gates) = await CreateOneSlotServer(CreateServerConfig(Type.Uds), 3);

        var first = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 0);
        Assert.That(server.GetSnapshot().Sessions.Single().Invocations.OldestQueuedWait, Is.Null);

        var second = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 1);
        time.Advance(TimeSpan.FromSeconds(3));
        var third = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 2);
        time.Advance(TimeSpan.FromSeconds(1));

        // The second has waited 4 s, the third 1 s.
        Assert.That(server.GetSnapshot().Sessions.Single().Invocations.OldestQueuedWait, Is.EqualTo(TimeSpan.FromSeconds(4)));

        gates[0].SetResult();
        await first.Timeout(1);
        await WaitForQueue(server, executing: 1, queued: 1);
        Assert.That(server.GetSnapshot().Sessions.Single().Invocations.OldestQueuedWait, Is.EqualTo(TimeSpan.FromSeconds(1)));

        gates[1].SetResult();
        await second.Timeout(1);
        await WaitForQueue(server, executing: 1, queued: 0);
        Assert.That(server.GetSnapshot().Sessions.Single().Invocations.OldestQueuedWait, Is.Null);

        gates[2].SetResult();
        await third.Timeout(1);
    }

    [Test]
    public async Task QueueWarning_NamesMethodAndWait_AtMostOncePer30Seconds()
    {
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.Logger = logger;
        serverConfig.InvocationQueueWarningThreshold = 1000;
        var (time, server, client, gates) = await CreateOneSlotServer(serverConfig, 4);
        IEnumerable<string> Warnings() => logger.Warnings.Where(w => w.Contains("execution slot"));

        var calls = new List<Task>();
        calls.Add(client.Proxy.ServerTask().AsTask());
        await WaitForQueue(server, executing: 1, queued: 0);
        calls.Add(client.Proxy.ServerTask().AsTask());
        calls.Add(client.Proxy.ServerTask().AsTask());
        await WaitForQueue(server, executing: 1, queued: 2);

        // Both queued calls wait 2 s, over the 1 s threshold; only the first of them is reported.
        time.Advance(TimeSpan.FromSeconds(2));
        gates[0].SetResult();
        await WaitForQueue(server, executing: 1, queued: 1);
        await Eventually(() => Warnings().Any());
        Assert.That(Warnings().Count(), Is.EqualTo(1));
        var warning = Warnings().Single();
        Assert.That(warning, Does.Match(@"method \d+"));
        Assert.That(warning, Does.Contain("2"));

        gates[1].SetResult();
        await WaitForQueue(server, executing: 1, queued: 0);
        await Task.Delay(100);
        Assert.That(Warnings().Count(), Is.EqualTo(1));

        // 31 s later the next slow one is reported again.
        calls.Add(client.Proxy.ServerTask().AsTask());
        await WaitForQueue(server, executing: 1, queued: 1);
        time.Advance(TimeSpan.FromSeconds(31));
        gates[2].SetResult();
        await WaitForQueue(server, executing: 1, queued: 0);
        await Eventually(() => Warnings().Count() >= 2);
        Assert.That(Warnings().Count(), Is.EqualTo(2));
        // The second queued call of the first round was suppressed; this warning counts it.
        Assert.That(Warnings().First(), Does.EndWith("threshold 1000 ms)."));
        Assert.That(Warnings().Last(), Does.Contain("(1 more since the last warning)."));

        gates[3].SetResult();
        await Task.WhenAll(calls).Timeout(2);
    }

    [Test]
    public async Task QueueWarning_ZeroThreshold_NeverWarns()
    {
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.Logger = logger;
        serverConfig.InvocationQueueWarningThreshold = 0;
        var (time, server, client, gates) = await CreateOneSlotServer(serverConfig, 2);

        var first = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 0);
        var second = client.Proxy.ServerTask().AsTask();
        await WaitForQueue(server, executing: 1, queued: 1);
        time.Advance(TimeSpan.FromSeconds(60));
        gates[0].SetResult();
        gates[1].SetResult();
        await Task.WhenAll(first, second).Timeout(2);

        // The wait is still measured; only the warning is off.
        await Eventually(() => _recorder.Events("nexnet.invocation.queue_wait").Count >= 2);
        Assert.That(_recorder.Events("nexnet.invocation.queue_wait").Count, Is.EqualTo(2));
        Assert.That(logger.Warnings.Where(w => w.Contains("execution slot")), Is.Empty);
    }

    [Test]
    public async Task PipeCreditWait_RecordedWhenTheWaitEnds()
    {
        // The server reads nothing until released, so the client's writer uses up the window and waits for credit
        // for 1.5 s on its fake clock.
        var serverConfig = CreateServerConfig(Type.Uds);
        var clientConfig = CreateClientConfig(Type.Uds);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        clientConfig.Time = time;
        clientConfig.MeterFactory = _recorder;
        clientConfig.Timeout = 300_000;
        clientConfig.ProgressTimeout = 600_000;
        var length = serverConfig.PipeWindow * 2;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await release.Task;
            long read = 0;
            while (read < length)
            {
                var result = await pipe.Input.ReadAsync();
                read += result.Buffer.Length;
                pipe.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                    break;
            }
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);

        var writing = pipe.Output.WriteAsync(new byte[length]).AsTask();
        await MetricsSessionTests.WaitFor(() => client.GetSnapshot()!.Pipes.SingleOrDefault()?.WaitingForCredit != null).Timeout(2);
        Assert.That(_recorder.Events("nexnet.pipe.credit_wait"), Is.Empty, "Recorded only when the wait ends.");

        time.Advance(TimeSpan.FromSeconds(1.5));
        release.SetResult();
        await writing.Timeout(2);

        await Eventually(() => _recorder.Events("nexnet.pipe.credit_wait").Count > 0);
        var waits = _recorder.Events("nexnet.pipe.credit_wait");
        Assert.That(waits, Is.Not.Empty);
        Assert.That(waits.Max(w => w.Value), Is.EqualTo(1.5));
        Assert.That(waits.Sum(w => w.Value), Is.EqualTo(1.5));
        Assert.That(waits.All(w => w.Has("nexnet.role", "client") && w.Has("nexnet.transport", "uds")), Is.True);
    }

    [Test]
    public async Task WaitForCreditAsync_RecordsPipeCreditWait()
    {
        using var recorder = new MetricsRecorder();
        var tags = recorder.Metrics.GetTagSet(false, "uds");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var writer = new NexNet.Pipes.NexusPipeWriter(null!, null,
            new NexNet.IntegrationTests.Pipes.NexusDuplexPipeWriterTests.SessionMessengerStub(),
            false, 1024, time, tags);

        var waiting = writer.WaitForCreditAsync(4096, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.That(waiting.IsCompleted, Is.False);

        time.Advance(TimeSpan.FromSeconds(1.5));
        writer.AddCredit(4096);
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        var waits = recorder.Events("nexnet.pipe.credit_wait");
        Assert.That(waits.Sum(w => w.Value), Is.EqualTo(1.5),
            "A credit wait through WaitForCreditAsync (pipe relays) ends without recording nexnet.pipe.credit_wait.");
    }
}
