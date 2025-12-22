using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Scalability;

/// <summary>
/// Measures performance under concurrent client load.
/// Multiple clients connect and invoke the server simultaneously to measure latency degradation.
/// </summary>
public sealed class MultiClientScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "MultiClient";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Scalability;

    /// <inheritdoc />
    public override string Description => "Concurrent client invocations measuring latency degradation";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => true;

    /// <summary>
    /// Uses Small payload for consistent testing across client counts.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Client counts to test. Quick mode uses fewer clients.
    /// </summary>
    private static readonly int[] FullClientCounts = [10, 25, 50, 100];
    private static readonly int[] QuickClientCounts = [5, 10, 25];

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var payload = context.GeneratePayload();
            var clientCounts = context.Settings.MeasuredIterations <= 5 ? QuickClientCounts : FullClientCounts;
            var allMeasurements = new List<double>();
            var customMetrics = new Dictionary<string, object>();
            var maxClientCount = 0;

            foreach (var clientCount in clientCounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                maxClientCount = Math.Max(maxClientCount, clientCount);

                // Create and connect all clients
                var clients = new List<(BenchmarkClientNexus nexus, NexusClient<BenchmarkClientNexus, BenchmarkClientNexus.ServerProxy> client)>();

                try
                {
                    for (int i = 0; i < clientCount; i++)
                    {
                        var clientNexus = new BenchmarkClientNexus();
                        var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
                        await client.ConnectAsync(cancellationToken);
                        clients.Add((clientNexus, client));
                    }

                    // Warmup - each client does a single echo
                    foreach (var (_, client) in clients)
                    {
                        await client.Proxy.Echo(payload);
                    }

                    // Measure: All clients invoke simultaneously
                    var latencies = new double[clientCount];
                    var tasks = new Task[clientCount];

                    var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    for (int i = 0; i < clientCount; i++)
                    {
                        var index = i;
                        var client = clients[i].client;

                        tasks[i] = Task.Run(async () =>
                        {
                            // Wait for start signal
                            await startSignal.Task;

                            var sw = Stopwatch.StartNew();
                            await client.Proxy.Echo(payload);
                            sw.Stop();

                            latencies[index] = sw.Elapsed.TotalMicroseconds;
                        }, cancellationToken);
                    }

                    // Release all clients simultaneously
                    startSignal.SetResult();
                    await Task.WhenAll(tasks);

                    // Record metrics for this client count
                    var stats = StatisticsCalculator.Calculate(latencies);
                    customMetrics[$"Clients_{clientCount}_P50_us"] = stats.P50;
                    customMetrics[$"Clients_{clientCount}_P95_us"] = stats.P95;
                    customMetrics[$"Clients_{clientCount}_P99_us"] = stats.P99;
                    customMetrics[$"Clients_{clientCount}_Mean_us"] = stats.Mean;

                    allMeasurements.AddRange(latencies);
                }
                finally
                {
                    // Disconnect all clients
                    foreach (var (_, client) in clients)
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

                // Small delay between client count tests
                await Task.Delay(100, cancellationToken);
            }

            return ScenarioResult.FromScalabilityMeasurement(
                Name,
                Category,
                context.Transport,
                context.PayloadSize,
                maxClientCount,
                allMeasurements,
                customMetrics: customMetrics);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
