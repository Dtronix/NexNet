using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Throughput;

/// <summary>
/// Measures sustained channel throughput for typed and unmanaged channels.
/// Tests managed (SmallPayload) and unmanaged (long) channel upload.
/// </summary>
public sealed class ChannelThroughputScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "ChannelThroughput";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Throughput;

    /// <inheritdoc />
    public override string Description => "Typed channel upload throughput (managed and unmanaged)";

    /// <summary>
    /// Channel throughput uses SmallPayload (~1KB) for managed channels.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

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
                // Create payload for managed channel tests
                var payload = PayloadFactory.CreateSmall(context.Settings.RandomSeed);
                var payloadSizeBytes = 1024; // Approximate SmallPayload size

                // Warmup manager
                var warmup = new WarmupManager(context.Settings);

                // === Managed Channel Upload ===
                await warmup.WarmupAsync(async () =>
                {
                    await using var channel = client.CreateChannel<SmallPayload>();
                    _ = client.Proxy.ChannelUpload(channel);

                    var writer = await channel.GetWriterAsync();
                    for (int i = 0; i < 50; i++)
                    {
                        await writer.WriteAsync(payload);
                    }
                    await writer.CompleteAsync();
                }, "Managed Channel Upload");

                // Measure managed upload
                long managedUploadItems = 0;
                TimeSpan managedUploadDuration;
                {
                    await using var channel = client.CreateChannel<SmallPayload>();
                    _ = client.Proxy.ChannelUpload(channel);

                    var writer = await channel.GetWriterAsync();
                    var sw = Stopwatch.StartNew();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(context.Settings.ThroughputDuration);

                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            if (!await writer.WriteAsync(payload, cts.Token))
                                break;
                            managedUploadItems++;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Expected when test duration expires
                    }
                    catch (Exception)
                    {
                        // Ignore other errors during cancellation
                    }

                    sw.Stop();
                    managedUploadDuration = sw.Elapsed;

                    try
                    {
                        await writer.CompleteAsync();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                // Small delay
                await Task.Delay(100, cancellationToken);

                // === Unmanaged Channel Upload ===
                await warmup.WarmupAsync(async () =>
                {
                    await using var channel = client.CreateUnmanagedChannel<long>();
                    _ = client.Proxy.UnmanagedChannelUpload(channel);

                    var writer = await channel.GetWriterAsync();
                    for (long i = 0; i < 50; i++)
                    {
                        await writer.WriteAsync(i);
                    }
                    await writer.CompleteAsync();
                }, "Unmanaged Channel Upload");

                // Measure unmanaged upload
                long unmanagedUploadItems = 0;
                TimeSpan unmanagedUploadDuration;
                {
                    await using var channel = client.CreateUnmanagedChannel<long>();
                    _ = client.Proxy.UnmanagedChannelUpload(channel);

                    var writer = await channel.GetWriterAsync();
                    var sw = Stopwatch.StartNew();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(context.Settings.ThroughputDuration);

                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            if (!await writer.WriteAsync(unmanagedUploadItems, cts.Token))
                                break;
                            unmanagedUploadItems++;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Expected when test duration expires
                    }
                    catch (Exception)
                    {
                        // Ignore other errors during cancellation
                    }

                    sw.Stop();
                    unmanagedUploadDuration = sw.Elapsed;

                    try
                    {
                        await writer.CompleteAsync();
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }

                // Calculate metrics
                var managedUploadBytes = managedUploadItems * payloadSizeBytes;
                var unmanagedBytes = 8; // sizeof(long)

                var customMetrics = new Dictionary<string, object>
                {
                    // Managed channel metrics
                    ["Managed_Upload_ItemsPerSec"] = managedUploadItems / managedUploadDuration.TotalSeconds,
                    ["Managed_Upload_MBps"] = managedUploadBytes / (1024.0 * 1024.0) / managedUploadDuration.TotalSeconds,

                    // Unmanaged channel metrics
                    ["Unmanaged_Upload_ItemsPerSec"] = unmanagedUploadItems / unmanagedUploadDuration.TotalSeconds,
                    ["Unmanaged_Upload_MBps"] = (unmanagedUploadItems * unmanagedBytes) / (1024.0 * 1024.0) / unmanagedUploadDuration.TotalSeconds,
                };

                // Primary metrics: managed upload
                return ScenarioResult.FromThroughputMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    managedUploadBytes,
                    managedUploadItems,
                    managedUploadDuration,
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
