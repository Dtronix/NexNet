using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Latency;

/// <summary>
/// Measures round-trip latency for duplex pipe communication.
/// Uses the bidirectional echo mode where server echoes back received data.
/// </summary>
public sealed class PipeLatencyScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "PipeLatency";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Latency;

    /// <inheritdoc />
    public override string Description => "Duplex pipe single-message round-trip latency";

    /// <summary>
    /// Supports all payload sizes. For latency testing, Tiny/Small/Medium are most relevant.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } =
        [PayloadSize.Tiny, PayloadSize.Small, PayloadSize.Medium];

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            // Create and connect client
            var clientNexus = new BenchmarkClientNexus();
            var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
            await client.ConnectAsync(cancellationToken);

            try
            {
                // Generate payload for this test
                var payload = context.GeneratePayload();

                // Create warmup manager
                var warmup = new WarmupManager(context.Settings);

                // Warmup - establish pipe connections to warm up the path
                await warmup.WarmupAsync(async () =>
                {
                    await using var pipe = client.CreatePipe();
                    // Start the bidirectional stream - server will echo back
                    _ = client.Proxy.StreamBidirectional(pipe);
                    await pipe.ReadyTask;

                    // Write data
                    await pipe.Output.WriteAsync(payload);

                    // Read echoed data
                    var result = await pipe.Input.ReadAsync();
                    pipe.Input.AdvanceTo(result.Buffer.End);

                    // Complete the pipe
                    await pipe.CompleteAsync();
                }, "Pipe Echo");

                // Measure iterations
                var measurements = await warmup.MeasureAsync(async () =>
                {
                    await using var pipe = client.CreatePipe();

                    var sw = Stopwatch.StartNew();

                    // Start the bidirectional stream - server will echo back
                    _ = client.Proxy.StreamBidirectional(pipe);
                    await pipe.ReadyTask;

                    // Write data
                    await pipe.Output.WriteAsync(payload);

                    // Read echoed data back
                    long bytesRead = 0;
                    while (bytesRead < payload.Length)
                    {
                        var result = await pipe.Input.ReadAsync();
                        if (result.IsCompleted || result.IsCanceled)
                            break;

                        bytesRead += result.Buffer.Length;
                        pipe.Input.AdvanceTo(result.Buffer.End);
                    }

                    sw.Stop();

                    // Complete the pipe
                    await pipe.CompleteAsync();

                    return sw.Elapsed.TotalMicroseconds;
                }, "Pipe Echo");

                return ScenarioResult.FromLatencyMeasurements(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    measurements);
            }
            finally
            {
                await client.DisconnectAsync();
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
