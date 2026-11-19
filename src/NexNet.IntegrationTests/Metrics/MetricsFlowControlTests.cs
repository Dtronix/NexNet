using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Flow-control gauges (budget, pipes, invocations) and the counters for resets, refusals and aborts.
/// </summary>
[NotInParallel("stress")]
internal class MetricsFlowControlTests : BaseTests
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

    private double Sum(string instrument, string role) => _recorder.ObserveSum(instrument, ("nexnet.role", role));

    [Test]
    public async Task OpenPipe_CountedWithItsWindowAndBudget()
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        var clientConfig = CreateClientConfig(Type.Uds);
        serverConfig.MeterFactory = _recorder;
        clientConfig.MeterFactory = _recorder;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, _) => await release.Task);
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(1);

        await Assert.That(Sum("nexnet.budget.limit", "server")).IsEqualTo(serverConfig.SessionBudget);
        var baseline = Sum("nexnet.budget.reserved", "server");
        await Assert.That(baseline).IsEqualTo((double)serverConfig.InvocationCredits * 64 * 1024);

        var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);

        await MetricsSessionTests.WaitFor(() => Sum("nexnet.pipe.open", "server") == 1 && Sum("nexnet.pipe.open", "client") == 1).Timeout(1);
        await Assert.That(Sum("nexnet.pipe.window", "server")).IsEqualTo(serverConfig.PipeWindow);
        await Assert.That(Sum("nexnet.budget.reserved", "server")).IsEqualTo(baseline + serverConfig.PipeWindow);

        // Freed once both sides closed it and both applications released it.
        release.SetResult();
        await pipe.DisposeAsync();
        await MetricsSessionTests.WaitFor(() => Sum("nexnet.pipe.open", "server") == 0 && Sum("nexnet.pipe.open", "client") == 0).Timeout(2);
        await Assert.That(Sum("nexnet.budget.reserved", "server")).IsEqualTo(baseline);
    }

    [Test]
    public async Task BlockedInvocations_CountedAsExecutingQueuedAndWaitingForCredit()
    {
        // One slot and one credit on the server: the first call runs, the second is queued with the credit, the
        // third waits on the client for credit.
        var serverConfig = CreateServerConfig(Type.Uds);
        var clientConfig = CreateClientConfig(Type.Uds);
        serverConfig.MeterFactory = _recorder;
        clientConfig.MeterFactory = _recorder;
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        serverConfig.InvocationCredits = 1;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskEvent = async _ => await release.Task);
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(1);

        var calls = Enumerable.Range(0, 3).Select(_ => client.Proxy.ServerTask().AsTask()).ToArray();

        await MetricsSessionTests.WaitFor(() =>
            Sum("nexnet.invocation.executing", "server") == 1
            && Sum("nexnet.invocation.queued", "server") == 1
            && Sum("nexnet.invocation.credit_waits", "client") == 1).Timeout(2);

        release.SetResult();
        await Task.WhenAll(calls).Timeout(2);

        await MetricsSessionTests.WaitFor(() =>
            Sum("nexnet.invocation.executing", "server") == 0
            && Sum("nexnet.invocation.queued", "server") == 0
            && Sum("nexnet.invocation.credit_waits", "client") == 0).Timeout(2);
    }

    [Test]
    [Arguments(new byte[] { 0x80, 0x80, 0x80, 0x04 }, "too_large")]
    [Arguments(new byte[] { 0x01, 0xC1 }, "invalid_item")]
    public async Task BrokenChannelItem_RecordedAsResetWithReason(byte[] data, string reason)
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.MeterFactory = _recorder;
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            try
            {
                await foreach (var item in await pipe.GetChannel<int>().GetReaderAsync())
                {
                }
            }
            catch
            {
                // The reset ends the read.
            }
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        await pipe.Output.WriteAsync(data).Timeout(1);

        await MetricsSessionTests.WaitFor(() => _recorder.Events("nexnet.pipe.resets").Count == 1).Timeout(2);
        var reset = _recorder.Events("nexnet.pipe.resets").Single();
        await Assert.That(reset.Has("nexnet.reset.reason", reason)).IsTrue();
        await Assert.That(reset.Has("nexnet.role", "server")).IsTrue();
    }

    [Test]
    public async Task PipeOverTheLimit_RecordedAsLocalRefusal()
    {
        var clientConfig = CreateClientConfig(Type.Uds);
        clientConfig.MeterFactory = _recorder;
        clientConfig.MaxPipes = 1;
        var server = CreateServer(CreateServerConfig(Type.Uds), null);
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(1);

        await using var first = client.CreatePipe();
        Assert.ThrowsExactly<NexusBusyException>(() => client.CreatePipe());

        var refusal = _recorder.Events("nexnet.pipe.refusals").Single();
        await Assert.That(refusal.Has("nexnet.refusal.side", "local")).IsTrue();
        await Assert.That(refusal.Has("nexnet.role", "client")).IsTrue();
    }

    [Test]
    public async Task LargeArgumentWithoutBudget_RecordedAsBusyAbortOnBothSides()
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        var clientConfig = CreateClientConfig(Type.Uds);
        serverConfig.MeterFactory = _recorder;
        clientConfig.MeterFactory = _recorder;
        serverConfig.MaxMessageSize = 1024 * 1024;
        serverConfig.InvocationCredits = 4;
        serverConfig.MaxPipes = 1;
        serverConfig.PipeWindow = 64 * 1024;
        serverConfig.SessionBudget = 1536 * 1024;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerEchoEvent = async (_, data) =>
        {
            holding.TrySetResult();
            await hold.Task;
            return data;
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(1);

        var first = client.Proxy.ServerEcho(new byte[900 * 1024]).AsTask();
        await holding.Task.Timeout(5);
        await Assert.ThrowsAsync<NexusBusyException>(async () => await client.Proxy.ServerEcho(new byte[900 * 1024]).AsTask().Timeout(5));

        var aborts = _recorder.Events("nexnet.large_message.aborts");
        await Assert.That(aborts.Count(a => a.Has("nexnet.role", "server") && a.Has("nexnet.direction", "inbound")
                                      && a.Has("nexnet.abort.reason", "busy"))).IsEqualTo(1);
        await Assert.That(aborts.Count(a => a.Has("nexnet.role", "client") && a.Has("nexnet.direction", "outbound")
                                      && a.Has("nexnet.abort.reason", "busy"))).IsEqualTo(1);

        hold.SetResult();
        await first.Timeout(5);
        await MetricsSessionTests.WaitFor(() => _recorder.ObserveSum("nexnet.large_message.in_flight") == 0).Timeout(2);
    }
}