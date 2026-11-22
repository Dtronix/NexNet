using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Pipes;
using static NexNet.IntegrationTests.Metrics.ClientDiagnostics;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// The client's metrics (the C# meter's instruments; Java and JS <c>NexusMetrics</c>) on every row of the client
/// matrix, read through <see cref="IClientUnderTest.GetMetricsAsync"/>. The server side of each is covered by
/// <see cref="MetricsIoTests"/> and <see cref="MetricsFlowControlTests"/>.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientMetricsTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    private const string Role = "nexnet.role";

    private static void Native(ClientSpec spec) => spec.Metrics = MetricsRecording.Native;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task FramesAndBytes_MatchTheServer_AndSurviveDisconnect(ClientTarget target)
    {
        using var recorder = new MetricsRecorder();
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value),
            config => config.MeterFactory = recorder);
        await using var client = await ConnectAsync(target, server, configure: Native);

        for (var i = 0; i < 10; i++)
            await Proxy(client).ServerTaskValueWithParam(i).Timeout(T(1));

        double Server(string instrument, string direction)
            => recorder.ObserveSum(instrument, (Role, "server"), ("nexnet.direction", direction));
        static double Client(ClientMetrics m, string instrument, string direction)
            => m.Observe(instrument, (Role, "client"), ("nexnet.direction", direction));

        // Everything one side wrote, the other read: equal once nothing is in flight.
        var preamble = PreambleFrames(target);
        var metrics = await MetricsUntil(client, m =>
            Client(m, "nexnet.io.bytes", "sent") == Server("nexnet.io.bytes", "received")
            && Client(m, "nexnet.io.bytes", "received") == Server("nexnet.io.bytes", "sent")
            && Client(m, "nexnet.io.frames", "sent") == Server("nexnet.io.frames", "received") + preamble
            && Client(m, "nexnet.io.frames", "received") == Server("nexnet.io.frames", "sent"), T(3));
        await Assert.That(Client(metrics, "nexnet.io.bytes", "sent")).IsEqualTo(Server("nexnet.io.bytes", "received"));
        await Assert.That(Client(metrics, "nexnet.io.bytes", "received")).IsEqualTo(Server("nexnet.io.bytes", "sent"));
        await Assert.That(Client(metrics, "nexnet.io.frames", "sent")).IsEqualTo(Server("nexnet.io.frames", "received") + preamble);
        await Assert.That(Client(metrics, "nexnet.io.frames", "received")).IsEqualTo(Server("nexnet.io.frames", "sent"));

        var framesSent = Client(metrics, "nexnet.io.frames", "sent");
        var bytesSent = Client(metrics, "nexnet.io.bytes", "sent");
        await Assert.That(framesSent).IsGreaterThanOrEqualTo(11); // greeting + 10 invocations
        await Assert.That(bytesSent).IsGreaterThan(framesSent * 3);
        await Assert.That(metrics.Observe("nexnet.session.active", (Role, "client"))).IsEqualTo(1);
        await Assert.That(metrics.Observed.Where(m => m.Instrument == "nexnet.io.frames")
            .All(m => m.Has("nexnet.transport", TransportTag(target.Transport)))).IsTrue();

        // An ended session keeps counting: the counters never go back, and its disconnect is recorded once.
        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        metrics = await MetricsUntil(client, m => m.CountOf("nexnet.session.disconnects") == 1
                                                  && m.Observe("nexnet.session.active", (Role, "client")) == 0, T(2));
        await Assert.That(Client(metrics, "nexnet.io.frames", "sent")).IsGreaterThan(framesSent);
        await Assert.That(Client(metrics, "nexnet.io.bytes", "sent")).IsGreaterThan(bytesSent);
        await Assert.That(metrics.Observe("nexnet.session.active", (Role, "client"))).IsEqualTo(0);
        var disconnect = metrics.EventsOf("nexnet.session.disconnects").Single();
        await Assert.That(disconnect.HasAll((Role, "client"), ("nexnet.session.phase", "established"))).IsTrue();
        await Assert.That(metrics.CountOf("nexnet.session.duration", (Role, "client"))).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task OpenPipe_CountedWithItsWindowAndBudget(ClientTarget target)
    {
        const int window = 32 * 1024;
        await using var server = await StartServerAsync(nexus =>
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask);
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            Native(spec);
            spec.PipeWindow = window;
        });

        var metrics = await client.GetMetricsAsync().Timeout(T(1));
        var baseline = metrics.Observe("nexnet.budget.reserved");
        await Assert.That(metrics.Observe("nexnet.budget.limit")).IsEqualTo((await client.GetBudgetAsync())!.Limit);
        await Assert.That(metrics.Observe("nexnet.pipe.open")).IsEqualTo(0);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            metrics = await MetricsUntil(client, m => m.Observe("nexnet.pipe.open") == 1
                                                      && m.Observe("nexnet.budget.reserved") == baseline + window, T(2));
            await Assert.That(metrics.Observe("nexnet.pipe.open", (Role, "client"))).IsEqualTo(1);
            await Assert.That(metrics.Observe("nexnet.pipe.window", (Role, "client"))).IsEqualTo(window);
            await Assert.That(metrics.Observe("nexnet.budget.reserved", (Role, "client"))).IsEqualTo(baseline + window);
        }

        // Freed once both sides closed it.
        metrics = await MetricsUntil(client, m => m.Observe("nexnet.pipe.open") == 0 && m.Observe("nexnet.budget.reserved") == baseline, T(2));
        await Assert.That(metrics.Observe("nexnet.pipe.open")).IsEqualTo(0);
        await Assert.That(metrics.Observe("nexnet.pipe.window")).IsEqualTo(0);
        await Assert.That(metrics.Observe("nexnet.budget.reserved")).IsEqualTo(baseline);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task CallsWithoutCredit_CountedAsCreditWaits(ClientTarget target)
    {
        // One slot and one credit on the server: the first call runs, the second is queued with the credit, the
        // third waits on the client for credit.
        var release = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskEvent = async _ => await release.Task, config =>
        {
            config.MaxConcurrentConnectionInvocations = 1;
            config.InvocationCredits = 1;
        });
        await using var client = await ConnectAsync(target, server, configure: Native);

        var calls = Enumerable.Range(0, 3).Select(_ => Proxy(client).ServerTask().AsTask()).ToArray();
        var metrics = await MetricsUntil(client, m => m.Observe("nexnet.invocation.credit_waits") == 1, T(2));
        await Assert.That(metrics.Observe("nexnet.invocation.credit_waits", (Role, "client"))).IsEqualTo(1);

        release.SetResult();
        await Task.WhenAll(calls).Timeout(T(2));
        metrics = await MetricsUntil(client, m => m.Observe("nexnet.invocation.credit_waits") == 0, T(2));
        await Assert.That(metrics.Observe("nexnet.invocation.credit_waits")).IsEqualTo(0);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task PipeOverTheLimit_RecordedAsLocalRefusal(ClientTarget target)
    {
        await using var server = await StartServerAsync(nexus =>
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) => await pipe.CompleteTask);
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            Native(spec);
            spec.MaxPipes = 1;
        });

        await using (var first = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(first).Timeout(T(1));
            await first.WhenReadyAsync().AsTask().Timeout(T(1));
            await Assert.ThrowsExactlyAsync<NexusBusyException>(async () =>
            {
                await using var second = await client.CreatePipeAsync();
                await Proxy(client).ServerTaskValueWithDuplexPipe(second).Timeout(T(1));
            });
        }

        var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.pipe.refusals") > 0, T(1));
        var refusal = metrics.EventsOf("nexnet.pipe.refusals").Single();
        await Assert.That(refusal.HasAll(("nexnet.refusal.side", "local"), (Role, "client"))).IsTrue();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task LargeArgumentWithoutBudget_RecordedAsBusyAbort(ClientTarget target)
    {
        // The server has room for one 900 KiB argument: the second one is refused as busy while the first is held.
        var hold = Signal();
        var holding = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerEchoEvent = async (_, data) =>
        {
            holding.TrySetResult();
            await hold.Task;
            return data;
        }, config =>
        {
            config.MaxMessageSize = 1024 * 1024;
            config.InvocationCredits = 4;
            config.MaxPipes = 1;
            config.PipeWindow = 64 * 1024;
            config.SessionBudget = 1536 * 1024;
        });
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            Native(spec);
            spec.MaxMessageSize = 1024 * 1024;
        });

        var first = Proxy(client).ServerEcho(new byte[900 * 1024]);
        await holding.Task.Timeout(T(5));
        await Assert.ThrowsExactlyAsync<NexusBusyException>(async () => await Proxy(client).ServerEcho(new byte[900 * 1024]).Timeout(T(5)));

        var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.large_message.aborts") > 0, T(1));
        await Assert.That(metrics.CountOf("nexnet.large_message.aborts",
            (Role, "client"), ("nexnet.direction", "outbound"), ("nexnet.abort.reason", "busy"))).IsEqualTo(1);

        hold.SetResult();
        await first.Timeout(T(5));
        metrics = await MetricsUntil(client, m => m.Observe("nexnet.large_message.in_flight") == 0, T(2));
        await Assert.That(metrics.Observe("nexnet.large_message.in_flight")).IsEqualTo(0);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task QueueWait_RecordedForEveryInvocationFromQueuedToSlot(ClientTarget target)
    {
        // One execution slot on the client: the second call from the server waits for the first (300 ms).
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            script => script.ClientTaskWithParam.Delay(300),
            spec =>
            {
                Native(spec);
                spec.MaxConcurrentConnectionInvocations = 1;
            });

        using (var context = server.Server.ContextProvider.Rent())
        {
            await context.Proxy.All.ClientTaskWithParam(1).Timeout(T(1));
            await context.Proxy.All.ClientTaskWithParam(2).Timeout(T(1));
        }

        var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.invocation.queue_wait") >= 2, T(3));
        var waits = metrics.EventsOf("nexnet.invocation.queue_wait");
        await Assert.That(waits.Count).IsEqualTo(2);
        await Assert.That(waits.All(w => w.HasAll((Role, "client"), ("nexnet.invocation.pool", "normal")))).IsTrue();
        await Assert.That(waits.Min(w => w.Value)).IsLessThan(0.2);
        await Assert.That(waits.Max(w => w.Value)).IsGreaterThanOrEqualTo(0.2);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket], Requires = ClientCapabilities.FakeTime)]
    public async Task PipeCreditWait_RecordedWhenTheWaitEnds(ClientTarget target)
    {
        // The server reads nothing until released, so the client's writer uses up the window and waits for credit
        // for 1.5 s on its test clock.
        var length = 0;
        var release = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
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
        }, config => length = config.PipeWindow * 2);
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            Native(spec);
            spec.FakeTime = true;
        });

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));

            var writing = pipe.WriteAsync(new byte[length]).AsTask();
            var snapshot = await SnapshotUntil(client, s => s.Pipes.SingleOrDefault()?.WaitingForCredit != null, T(2));
            await Assert.That(snapshot!.Pipes.Single().WaitingForCredit).IsNotNull();
            await Assert.That((await client.GetMetricsAsync()).EventsOf("nexnet.pipe.credit_wait")).IsEmpty()
                .Because("Recorded only when the wait ends.");

            await client.AdvanceTimeAsync(TimeSpan.FromSeconds(1.5)).Timeout(T(1));
            release.SetResult();
            await writing.Timeout(T(2));
        }

        var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.pipe.credit_wait") > 0, T(2));
        var waits = metrics.EventsOf("nexnet.pipe.credit_wait");
        await Assert.That(waits).IsNotEmpty();
        await Assert.That(waits.Max(w => w.Value)).IsEqualTo(1.5);
        await Assert.That(waits.Sum(w => w.Value)).IsEqualTo(1.5);
        await Assert.That(waits.All(w => w.HasAll((Role, "client"), ("nexnet.transport", TransportTag(target.Transport))))).IsTrue();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Metrics_WithoutRecording_IsAnInvalidOperation(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await client.GetMetricsAsync().Timeout(T(1)));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task SessionLifecycle_Measured_AlsoThroughMicrometer(ClientTarget target)
    {
        // The Java rows record through the Java client's Micrometer module (NexusMicrometerBinder, read back from the
        // registry: counters and summaries aggregated), the other languages natively. The same assertions hold for both.
        var mode = target.Language == ClientLanguages.Java ? MetricsRecording.Micrometer : MetricsRecording.Native;
        using var recorder = new MetricsRecorder();
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value),
            config => config.MeterFactory = recorder);
        await using var client = await ConnectAsync(target, server, configure: spec => spec.Metrics = mode);

        for (var i = 0; i < 10; i++)
            await Proxy(client).ServerTaskValueWithParam(i).Timeout(T(1));

        var preamble = PreambleFrames(target);
        var metrics = await MetricsUntil(client, m =>
            m.Observe("nexnet.io.frames", (Role, "client"), ("nexnet.direction", "sent"))
            == recorder.ObserveSum("nexnet.io.frames", (Role, "server"), ("nexnet.direction", "received")) + preamble, T(3));
        await Assert.That(metrics.Observe("nexnet.io.frames", (Role, "client"), ("nexnet.direction", "sent")))
            .IsEqualTo(recorder.ObserveSum("nexnet.io.frames", (Role, "server"), ("nexnet.direction", "received")) + preamble);
        await Assert.That(metrics.Observe("nexnet.io.frames", (Role, "client"), ("nexnet.direction", "sent"))).IsGreaterThanOrEqualTo(11);
        await Assert.That(metrics.Observe("nexnet.session.active", (Role, "client"))).IsEqualTo(1);
        await Assert.That(metrics.Observe("nexnet.budget.limit", (Role, "client"))).IsEqualTo((await client.GetBudgetAsync())!.Limit);

        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        metrics = await MetricsUntil(client, m => m.CountOf("nexnet.session.disconnects") == 1, T(2));
        await Assert.That(metrics.CountOf("nexnet.session.disconnects", (Role, "client"), ("nexnet.session.phase", "established"))).IsEqualTo(1);
        await Assert.That(metrics.CountOf("nexnet.session.duration", (Role, "client"))).IsEqualTo(1);
        await Assert.That(metrics.Observe("nexnet.session.active", (Role, "client"))).IsEqualTo(0);
        await EndAsync(client);
    }
}
