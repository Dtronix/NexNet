using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Scalability;

/// <summary>
/// Measures server broadcast performance to multiple clients.
/// Tests delivery time spread from first to last client receiving messages.
/// </summary>
public sealed class BroadcastScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "Broadcast";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Scalability;

    /// <inheritdoc />
    public override string Description => "Server broadcasting to all connected clients";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => true;

    /// <summary>
    /// Uses Small payload for broadcast testing.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Client counts to test for broadcast.
    /// </summary>
    private static readonly int[] FullClientCounts = [10, 50, 100];
    private static readonly int[] QuickClientCounts = [5, 10, 25];

    /// <summary>
    /// Number of broadcast messages per test.
    /// </summary>
    private const int MessagesPerTest = 10;

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
            var allDeliverySpreadsMicroseconds = new List<double>();
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

                    // Register all clients for broadcast
                    foreach (var (_, client) in clients)
                    {
                        await client.Proxy.RegisterForBroadcast();
                    }

                    // Reset metrics on all clients
                    foreach (var (nexus, _) in clients)
                    {
                        nexus.ResetMetrics();
                    }

                    // Warmup - send a few broadcasts
                    for (int i = 0; i < 3; i++)
                    {
                        await clients[0].client.Proxy.RequestBroadcast(payload);
                        await Task.Delay(50, cancellationToken);
                    }

                    // Reset metrics after warmup
                    foreach (var (nexus, _) in clients)
                    {
                        nexus.ResetMetrics();
                    }

                    var deliverySpreads = new List<double>();
                    var broadcastLatencies = new List<double>();

                    // Measure broadcast delivery
                    for (int msg = 0; msg < MessagesPerTest; msg++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Reset metrics before each broadcast
                        foreach (var (nexus, _) in clients)
                        {
                            nexus.ResetMetrics();
                        }

                        // Send broadcast and measure
                        var sw = Stopwatch.StartNew();
                        await clients[0].client.Proxy.RequestBroadcast(payload);

                        // Wait for all clients to receive (with timeout)
                        var receiveTasks = clients.Select(c => c.nexus.WaitForBroadcastsAsync(1, cancellationToken));
                        await Task.WhenAll(receiveTasks);
                        sw.Stop();

                        broadcastLatencies.Add(sw.Elapsed.TotalMicroseconds);

                        // Calculate delivery spread
                        long firstTimestamp = long.MaxValue;
                        long lastTimestamp = long.MinValue;

                        foreach (var (nexus, _) in clients)
                        {
                            var first = nexus.FirstBroadcastTimestamp;
                            var last = nexus.LastBroadcastTimestamp;

                            if (first > 0 && first < firstTimestamp)
                                firstTimestamp = first;
                            if (last > lastTimestamp)
                                lastTimestamp = last;
                        }

                        if (firstTimestamp != long.MaxValue && lastTimestamp != long.MinValue)
                        {
                            var spreadTicks = lastTimestamp - firstTimestamp;
                            var spreadMicroseconds = (double)spreadTicks / Stopwatch.Frequency * 1_000_000;
                            deliverySpreads.Add(spreadMicroseconds);
                        }

                        // Small delay between messages
                        await Task.Delay(10, cancellationToken);
                    }

                    // Record metrics for this client count
                    if (deliverySpreads.Count > 0)
                    {
                        var stats = StatisticsCalculator.Calculate(deliverySpreads);
                        customMetrics[$"Clients_{clientCount}_SpreadP50_us"] = stats.P50;
                        customMetrics[$"Clients_{clientCount}_SpreadP95_us"] = stats.P95;
                        customMetrics[$"Clients_{clientCount}_SpreadMean_us"] = stats.Mean;
                        allDeliverySpreadsMicroseconds.AddRange(deliverySpreads);
                    }

                    if (broadcastLatencies.Count > 0)
                    {
                        var latencyStats = StatisticsCalculator.Calculate(broadcastLatencies);
                        customMetrics[$"Clients_{clientCount}_LatencyMean_us"] = latencyStats.Mean;
                    }

                    // Unregister from broadcast
                    foreach (var (_, client) in clients)
                    {
                        try
                        {
                            await client.Proxy.UnregisterFromBroadcast();
                        }
                        catch
                        {
                            // Ignore unregister errors
                        }
                    }
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

                // Delay between client count tests
                await Task.Delay(100, cancellationToken);
            }

            // Calculate overall first/last delivery from spreads
            double firstDelivery = 0;
            double lastDelivery = 0;
            if (allDeliverySpreadsMicroseconds.Count > 0)
            {
                var spreadStats = StatisticsCalculator.Calculate(allDeliverySpreadsMicroseconds);
                lastDelivery = spreadStats.Mean; // Average spread represents typical last delivery offset
            }

            return ScenarioResult.FromScalabilityMeasurement(
                Name,
                Category,
                context.Transport,
                context.PayloadSize,
                maxClientCount,
                allDeliverySpreadsMicroseconds,
                firstDelivery,
                lastDelivery,
                customMetrics);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
