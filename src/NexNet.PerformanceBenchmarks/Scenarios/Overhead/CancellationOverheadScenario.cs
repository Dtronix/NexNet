using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Overhead;

/// <summary>
/// Measures the overhead of CancellationToken propagation across invocations.
/// Compares latency of Echo (no token) vs EchoWithCancellation (with token).
/// </summary>
public sealed class CancellationOverheadScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "CancellationOverhead";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Overhead;

    /// <inheritdoc />
    public override string Description => "CancellationToken propagation overhead comparison";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// Uses Small payload to minimize serialization overhead and focus on token overhead.
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
                var warmup = new WarmupManager(context.Settings);
                var payload = PayloadFactory.CreateRawPayload(1024); // 1KB payload

                // Measure without CancellationToken
                await warmup.WarmupAsync(async () => { await client.Proxy.Echo(payload); }, "Echo without CT");
                var withoutToken = await warmup.MeasureAsync(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    await client.Proxy.Echo(payload);
                    sw.Stop();
                    return sw.Elapsed.TotalMicroseconds;
                }, "Echo without CT");

                // Measure with CancellationToken
                using var cts = new CancellationTokenSource();
                await warmup.WarmupAsync(async () => { await client.Proxy.EchoWithCancellation(payload, cts.Token); }, "Echo with CT");
                var withToken = await warmup.MeasureAsync(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    await client.Proxy.EchoWithCancellation(payload, cts.Token);
                    sw.Stop();
                    return sw.Elapsed.TotalMicroseconds;
                }, "Echo with CT");

                // Calculate overhead
                var statsWithout = StatisticsCalculator.Calculate(withoutToken);
                var statsWith = StatisticsCalculator.Calculate(withToken);
                var overheadMean = statsWith.Mean - statsWithout.Mean;
                var overheadP50 = statsWith.P50 - statsWithout.P50;
                var overheadP95 = statsWith.P95 - statsWithout.P95;
                var overheadP99 = statsWith.P99 - statsWithout.P99;
                var overheadPercent = statsWithout.Mean > 0 ? (overheadMean / statsWithout.Mean) * 100 : 0;

                var customMetrics = new Dictionary<string, object>
                {
                    ["WithoutToken_Mean_us"] = statsWithout.Mean,
                    ["WithoutToken_P50_us"] = statsWithout.P50,
                    ["WithoutToken_P95_us"] = statsWithout.P95,
                    ["WithoutToken_P99_us"] = statsWithout.P99,
                    ["WithToken_Mean_us"] = statsWith.Mean,
                    ["WithToken_P50_us"] = statsWith.P50,
                    ["WithToken_P95_us"] = statsWith.P95,
                    ["WithToken_P99_us"] = statsWith.P99,
                    ["Overhead_Mean_us"] = overheadMean,
                    ["Overhead_P50_us"] = overheadP50,
                    ["Overhead_P95_us"] = overheadP95,
                    ["Overhead_P99_us"] = overheadP99,
                    ["Overhead_Percent"] = overheadPercent,
                    ["Iterations"] = statsWith.Count
                };

                // Return the "with token" latency as the primary metric
                // Custom metrics contain the comparison
                return new ScenarioResult
                {
                    ScenarioName = Name,
                    Category = Category,
                    Transport = context.Transport,
                    PayloadSize = context.PayloadSize,
                    LatencyMetrics = new LatencyMetrics
                    {
                        MeanMicroseconds = statsWith.Mean,
                        StdDevMicroseconds = statsWith.StdDev,
                        MinMicroseconds = statsWith.Min,
                        MaxMicroseconds = statsWith.Max,
                        P50Microseconds = statsWith.P50,
                        P95Microseconds = statsWith.P95,
                        P99Microseconds = statsWith.P99,
                        IterationCount = statsWith.Count
                    },
                    CustomMetrics = customMetrics
                };
            }
            finally
            {
                try
                {
                    await client.DisconnectAsync();
                }
                catch
                {
                    // Ignore disconnect errors
                }
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
