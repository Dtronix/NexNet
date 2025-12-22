using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Throughput;

/// <summary>
/// Measures method invocation throughput.
/// Tests both fire-and-forget (maximum throughput) and round-trip (with response) patterns.
/// </summary>
public sealed class InvocationThroughputScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "InvocationThroughput";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Throughput;

    /// <inheritdoc />
    public override string Description => "Rapid-fire RPC invocations (fire-and-forget and round-trip)";

    /// <summary>
    /// Limited to Tiny and Small payloads due to NexNet's 65521 byte method argument limit.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } =
        [PayloadSize.Tiny, PayloadSize.Small];

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

                var warmup = new WarmupManager(context.Settings);

                // === Fire-and-Forget Throughput ===
                // Reset server counter
                await client.Proxy.ResetFireAndForgetCount();

                // Warmup fire-and-forget
                await warmup.WarmupAsync(async () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        client.Proxy.FireAndForgetCounted(payload);
                    }
                    // Wait briefly for messages to arrive
                    await Task.Delay(50);
                }, "Fire-and-Forget");

                // Reset counter before measurement
                await client.Proxy.ResetFireAndForgetCount();

                // Measure fire-and-forget throughput
                var (fireForgetSent, fireForgetDuration) = await MeasureFireAndForgetAsync(
                    client.Proxy, payload, context.Settings.ThroughputDuration, cancellationToken);

                // Wait a bit for all messages to arrive at server
                await Task.Delay(500, cancellationToken);

                // Get actual received count from server
                var fireForgetReceived = await client.Proxy.GetFireAndForgetCount();

                // Small delay
                await Task.Delay(100, cancellationToken);

                // === Round-Trip Throughput (with response) ===
                // Warmup round-trip
                await warmup.WarmupAsync(async () =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        await client.Proxy.Echo(payload);
                    }
                }, "Round-Trip Echo");

                // Measure round-trip throughput
                var (roundTripCount, roundTripDuration) = await MeasureRoundTripAsync(
                    client.Proxy, payload, context.Settings.ThroughputDuration, cancellationToken);

                // Calculate metrics
                var fireForgetBytes = fireForgetSent * payloadSize;
                var roundTripBytes = roundTripCount * payloadSize * 2; // Request + Response

                var customMetrics = new Dictionary<string, object>
                {
                    // Fire-and-forget metrics
                    ["FireForget_Sent"] = fireForgetSent,
                    ["FireForget_Received"] = fireForgetReceived,
                    ["FireForget_InvocationsPerSec"] = fireForgetSent / fireForgetDuration.TotalSeconds,
                    ["FireForget_MBps"] = fireForgetBytes / (1024.0 * 1024.0) / fireForgetDuration.TotalSeconds,
                    ["FireForget_LossPercent"] = fireForgetSent > 0
                        ? (1.0 - (double)fireForgetReceived / fireForgetSent) * 100.0
                        : 0.0,

                    // Round-trip metrics
                    ["RoundTrip_Count"] = roundTripCount,
                    ["RoundTrip_InvocationsPerSec"] = roundTripCount / roundTripDuration.TotalSeconds,
                    ["RoundTrip_MBps"] = roundTripBytes / (1024.0 * 1024.0) / roundTripDuration.TotalSeconds,
                };

                // Primary metrics: fire-and-forget (highest throughput mode)
                return ScenarioResult.FromThroughputMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    fireForgetBytes,
                    fireForgetSent,
                    fireForgetDuration,
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

    private static async Task<(long sent, TimeSpan duration)> MeasureFireAndForgetAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        byte[] payload,
        TimeSpan testDuration,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        long totalSent = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(testDuration);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Fire-and-forget: void method
                proxy.FireAndForgetCounted(payload);
                totalSent++;

                // Yield occasionally to allow processing
                if (totalSent % 1000 == 0)
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Expected when test duration expires
        }

        sw.Stop();

        return (totalSent, sw.Elapsed);
    }

    private static async Task<(long count, TimeSpan duration)> MeasureRoundTripAsync(
        BenchmarkClientNexus.ServerProxy proxy,
        byte[] payload,
        TimeSpan testDuration,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        long totalCount = 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(testDuration);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                // Round-trip: wait for response
                _ = await proxy.Echo(payload);
                totalCount++;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Expected when test duration expires
        }

        sw.Stop();

        return (totalCount, sw.Elapsed);
    }
}
