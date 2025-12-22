using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Throughput;

/// <summary>
/// Measures sustained pipe throughput for upload and download streaming.
/// Tests data transfer rate over a fixed duration.
/// </summary>
public sealed class PipeThroughputScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "PipeThroughput";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Throughput;

    /// <inheritdoc />
    public override string Description => "Sustained duplex pipe upload/download throughput";

    /// <summary>
    /// Throughput tests use Small (1KB) payload.
    /// Larger payloads can cause timing issues with warmup and cancellation.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } =
        [PayloadSize.Small];

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
                var payloadSize = payload.Length;

                // Warmup
                var warmup = new WarmupManager(context.Settings);
                await warmup.WarmupAsync(async () =>
                {
                    await using var pipe = client.CreatePipe();
                    _ = client.Proxy.StreamUpload(pipe);
                    await pipe.ReadyTask;

                    // Write a few chunks
                    for (int i = 0; i < 10; i++)
                    {
                        await pipe.Output.WriteAsync(payload);
                    }

                    await pipe.CompleteAsync();
                }, "Pipe Upload");

                // Measure upload throughput
                long uploadBytes = 0;
                long uploadMessages = 0;
                TimeSpan uploadDuration;
                {
                    await using var pipe = client.CreatePipe();

                    // Start the upload stream
                    _ = client.Proxy.StreamUpload(pipe);
                    await pipe.ReadyTask;

                    var sw = Stopwatch.StartNew();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(context.Settings.ThroughputDuration);

                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var result = await pipe.Output.WriteAsync(payload, cts.Token);
                            if (result.IsCompleted || result.IsCanceled)
                                break;

                            uploadBytes += payload.Length;
                            uploadMessages++;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Expected when test duration expires
                    }
                    catch (Exception)
                    {
                        // Ignore other exceptions during cancellation
                    }

                    sw.Stop();
                    uploadDuration = sw.Elapsed;

                    try
                    {
                        await pipe.CompleteAsync();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                // Small delay between tests
                await Task.Delay(100, cancellationToken);

                // Warmup download
                await warmup.WarmupAsync(async () =>
                {
                    await using var pipe = client.CreatePipe();
                    _ = client.Proxy.StreamDownload(pipe);
                    await pipe.ReadyTask;

                    // Read a few chunks
                    long bytesRead = 0;
                    while (bytesRead < payloadSize * 10)
                    {
                        var result = await pipe.Input.ReadAsync();
                        if (result.IsCompleted || result.IsCanceled)
                            break;

                        bytesRead += result.Buffer.Length;
                        pipe.Input.AdvanceTo(result.Buffer.End);
                    }

                    await pipe.CompleteAsync();
                }, "Pipe Download");

                // Measure download throughput
                long downloadBytes = 0;
                long downloadMessages = 0;
                TimeSpan downloadDuration;
                {
                    await using var pipe = client.CreatePipe();

                    // Start the download stream
                    _ = client.Proxy.StreamDownload(pipe);
                    await pipe.ReadyTask;

                    var sw = Stopwatch.StartNew();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(context.Settings.ThroughputDuration);

                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var result = await pipe.Input.ReadAsync(cts.Token);
                            if (result.IsCompleted || result.IsCanceled)
                                break;

                            downloadBytes += result.Buffer.Length;
                            downloadMessages++;
                            pipe.Input.AdvanceTo(result.Buffer.End);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Expected when test duration expires
                    }
                    catch (Exception)
                    {
                        // Ignore other exceptions during cancellation
                    }

                    sw.Stop();
                    downloadDuration = sw.Elapsed;

                    try
                    {
                        await pipe.CompleteAsync();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                // Calculate combined metrics (use upload as primary, download as custom)
                var customMetrics = new Dictionary<string, object>
                {
                    ["Download_MBps"] = downloadBytes / (1024.0 * 1024.0) / downloadDuration.TotalSeconds,
                    ["Download_TotalBytes"] = downloadBytes,
                    ["Download_Duration_s"] = downloadDuration.TotalSeconds,
                    ["Upload_MBps"] = uploadBytes / (1024.0 * 1024.0) / uploadDuration.TotalSeconds
                };

                return ScenarioResult.FromThroughputMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    uploadBytes,
                    uploadMessages,
                    uploadDuration,
                    customMetrics);
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
