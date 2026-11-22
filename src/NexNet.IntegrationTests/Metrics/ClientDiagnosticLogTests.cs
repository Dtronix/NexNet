using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using static NexNet.IntegrationTests.Metrics.ClientDiagnostics;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// The client's diagnostic warnings and the metrics that go with them, on every row of the client matrix: a stalled
/// session, pipe resets, and the time invocations from the server wait for an execution slot. The client's log lines
/// reach <see cref="ClientSpec.Logger"/> (control protocol §7.4). The server side is covered by
/// <see cref="DiagnosticLogTests"/> and <see cref="InvocationQueueDiagnosticsTests"/>.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientDiagnosticLogTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task StalledItem_WarningNamesThePipe_AndDisconnectsAsStalled(ClientTarget target)
    {
        // The server sends the client the first 10 KiB of a 300 KiB channel item, and never the rest.
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var prefix = new byte[5];
            var prefixLength = ChannelFraming.WriteLength(prefix, 300 * 1024);
            await pipe.Output.WriteAsync(prefix.AsMemory(0, prefixLength));
            await pipe.Output.WriteAsync(new byte[10 * 1024]);
            await pipe.CompleteTask;
        });
        var logger = new CapturingLogger();
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            spec.Logger = logger;
            spec.LogLevel = NexusLogLevel.Warning;
            spec.Metrics = MetricsRecording.Native;
            spec.ProgressTimeoutMs = 300;
            spec.PingIntervalMs = 200;
        });

        var pipe = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
        var reader = await pipe.GetChannelReaderAsync<byte[]>();
        var reading = reader.ReadBatchAsync(1).AsTask();

        await Assert.That(await client.WhenDisconnectedAsync(T(5))).IsEqualTo(DisconnectReason.Stalled);
        await WaitUntil(() => logger.Warnings.Any(w => w.Contains("made no progress", StringComparison.Ordinal)));
        var warning = logger.Warnings.Single(w => w.Contains("made no progress", StringComparison.Ordinal));
        await Assert.That(warning).Contains("made no progress within 300 ms: ");
        await Assert.That(warning).Contains("pipe P");

        var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.session.disconnects") > 0, T(2));
        await Assert.That(metrics.CountOf("nexnet.session.disconnects", ("nexnet.role", "client"), ("nexnet.disconnect.reason", "stalled")))
            .IsEqualTo(1);

        try
        {
            await reading.Timeout(T(1));
        }
        catch (Exception)
        {
            // The session ended under the read.
        }

        await pipe.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task PipeResets_RecordedWithTheirReason_AndWarnedAtMostOncePer10Seconds(ClientTarget target)
    {
        // The server writes a channel item that breaks the framing into each pipe; the client reading it resets the pipe.
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await pipe.Output.WriteAsync(new byte[] { 0x01, 0xC1 });
            await pipe.CompleteTask;
        });
        var logger = new CapturingLogger();
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            spec.Logger = logger;
            spec.LogLevel = NexusLogLevel.Warning;
            spec.Metrics = MetricsRecording.Native;
        });

        for (var i = 1; i <= 3; i++)
        {
            await using var pipe = await client.CreatePipeAsync();
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            var reader = await pipe.GetChannelReaderAsync<int>();
            try
            {
                await reader.ReadBatchAsync(1).AsTask().Timeout(T(1));
            }
            catch (Exception)
            {
                // The reset ends the read.
            }

            var expected = i;
            var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.pipe.resets") == expected, T(2));
            await Assert.That(metrics.CountOf("nexnet.pipe.resets", ("nexnet.role", "client"), ("nexnet.reset.reason", "invalid_item")))
                .IsEqualTo(expected);
        }

        // The rate-limited warning: one for the three resets. The JavaScript client starts its messages with the session
        // ("S1: Reset pipe ..."), so the match is not anchored.
        var resets = logger.Warnings.Where(w => w.Contains("Reset pipe P", StringComparison.Ordinal)).ToList();
        await Assert.That(resets).Count().IsEqualTo(1);
        await Assert.That(resets[0]).Contains("a channel item breaks the framing.");

        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task QueueWarning_ZeroThreshold_NeverWarns_ButTheWaitIsRecorded(ClientTarget target)
    {
        var logger = new CapturingLogger();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            script => script.ClientTaskWithParam.Delay(300),
            spec =>
            {
                spec.Logger = logger;
                spec.LogLevel = NexusLogLevel.Warning;
                spec.Metrics = MetricsRecording.Native;
                spec.MaxConcurrentConnectionInvocations = 1;
                spec.InvocationQueueWarningThresholdMs = 0;
            });

        // One execution slot: the second call waits about 300 ms for it.
        using (var context = server.Server.ContextProvider.Rent())
        {
            await context.Proxy.All.ClientTaskWithParam(1).Timeout(T(1));
            await context.Proxy.All.ClientTaskWithParam(2).Timeout(T(1));
        }

        var metrics = await MetricsUntil(client, m => m.CountOf("nexnet.invocation.queue_wait") >= 2, T(3));
        await Assert.That(metrics.CountOf("nexnet.invocation.queue_wait")).IsEqualTo(2);
        await Assert.That(metrics.EventsOf("nexnet.invocation.queue_wait").Max(w => w.Value)).IsGreaterThanOrEqualTo(0.2);
        await RoundTripAsync(client);
        await Assert.That(logger.Warnings.Where(w => w.Contains("execution slot", StringComparison.Ordinal))).IsEmpty();
        await EndAsync(client);
    }
}
