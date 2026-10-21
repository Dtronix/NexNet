using NexNet.Pipes;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Log messages that say what went wrong: the stall that disconnected a session, and rate-limited pipe resets.
/// </summary>
internal class DiagnosticLogTests : BaseTests
{
    [Test]
    public async Task StalledItem_WarningNamesThePipeAndItsProgress()
    {
        using var recorder = new MetricsRecorder();
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.Logger = logger;
        serverConfig.MeterFactory = recorder;
        serverConfig.ProgressTimeout = 300;
        serverConfig.Timeout = 1000;
        var clientConfig = CreateClientConfig(Type.Uds);
        clientConfig.PingInterval = 200;
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            try
            {
                await foreach (var item in await pipe.GetChannel<byte[]>().GetReaderAsync())
                {
                }
            }
            catch
            {
                // The session ends.
            }
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(clientConfig);
        await client.ConnectAsync().Timeout(1);

        var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);

        // A 300 KiB item, larger than the 256 KiB window, of which only 10 KiB ever arrives.
        var prefix = new byte[5];
        var prefixLength = ChannelFraming.WriteLength(prefix, 300 * 1024);
        await pipe.Output.WriteAsync(prefix.AsMemory(0, prefixLength)).Timeout(1);
        await pipe.Output.WriteAsync(new byte[10 * 1024]).Timeout(1);

        await MetricsSessionTests.WaitFor(() => logger.Warnings.Any(w => w.Contains("made no progress"))).Timeout(5);
        var warning = logger.Warnings.Single(w => w.Contains("made no progress"));
        Assert.That(warning, Does.Contain("within 300 ms: pipe P"));
        Assert.That(warning, Does.Contain("item 10 KiB of 300 KiB, no data for"));

        await MetricsSessionTests.WaitFor(() => recorder.Events("nexnet.session.disconnects")
            .Any(e => e.Has("nexnet.disconnect.reason", "stalled"))).Timeout(2);
    }

    [Test]
    public async Task PipeResets_WarnAtMostOncePer10Seconds()
    {
        using var recorder = new MetricsRecorder();
        var logger = new CapturingLogger();
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.Logger = logger;
        serverConfig.MeterFactory = recorder;
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

        for (var i = 1; i <= 3; i++)
        {
            await using var pipe = client.CreatePipe();
            await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);
            await pipe.Output.WriteAsync(new byte[] { 0x01, 0xC1 }).Timeout(1);
            var expected = i;
            await MetricsSessionTests.WaitFor(() => recorder.Events("nexnet.pipe.resets").Count == expected).Timeout(2);
        }

        var resets = logger.Warnings.Where(w => w.StartsWith("Reset pipe", StringComparison.Ordinal)).ToList();
        Assert.That(resets, Has.Count.EqualTo(1));
        Assert.That(resets[0], Does.Contain("a channel item breaks the framing"));
    }
}
