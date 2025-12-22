using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Collections;

/// <summary>
/// Measures initial synchronization time for large collections.
/// Tests how long it takes for a client to receive the full collection state from the server.
/// </summary>
public sealed class LargeListSyncScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "LargeListSync";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Collections;

    /// <inheritdoc />
    public override string Description => "Initial collection synchronization time for varying list sizes";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// Uses Small payload for collection sync test.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// List sizes for full test.
    /// </summary>
    private static readonly int[] FullListSizes = [1_000, 10_000, 100_000];

    /// <summary>
    /// List sizes for quick test.
    /// </summary>
    private static readonly int[] QuickListSizes = [100, 1_000, 5_000];

    /// <summary>
    /// Data size per item in bytes (for memory calculation).
    /// </summary>
    private const int ItemDataSize = 100;

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var listSizes = context.Settings.MeasuredIterations <= 5 ? QuickListSizes : FullListSizes;
            var customMetrics = new Dictionary<string, object>();
            var allSyncTimes = new List<double>();
            var maxItemCount = 0;

            foreach (var listSize in listSizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                maxItemCount = Math.Max(maxItemCount, listSize);

                // Clear and populate server-side collection
                using var owner = server.ContextProvider.Rent();
                var serverList = owner.Collections.SyncList;

                // Clear existing items
                await serverList.ClearAsync();

                // Populate with test items
                for (int i = 0; i < listSize; i++)
                {
                    var item = TestItem.Create(i, ItemDataSize);
                    await serverList.AddAsync(item);
                }

                // Verify server list is populated
                if (serverList.Count != listSize)
                {
                    return ScenarioResult.Failed(
                        Name, Category, context.Transport, context.PayloadSize,
                        $"Failed to populate server list: expected {listSize}, got {serverList.Count}");
                }

                // Record initial memory state
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var initialMemory = GC.GetTotalMemory(false);

                // Create and connect client
                var clientNexus = new BenchmarkClientNexus();
                var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
                await client.ConnectAsync(cancellationToken);

                try
                {
                    // Get client-side collection reference
                    var clientList = client.Proxy.SyncList;

                    // Measure sync time
                    var sw = Stopwatch.StartNew();
                    await clientList.EnableAsync(cancellationToken);
                    await clientList.ReadyTask.WaitAsync(cancellationToken);
                    sw.Stop();

                    var syncTimeMs = sw.Elapsed.TotalMilliseconds;
                    allSyncTimes.Add(syncTimeMs);

                    // Verify client received all items
                    var clientCount = clientList.Count;
                    if (clientCount != listSize)
                    {
                        return ScenarioResult.Failed(
                            Name, Category, context.Transport, context.PayloadSize,
                            $"Client sync incomplete: expected {listSize}, got {clientCount}");
                    }

                    // Record final memory state
                    var finalMemory = GC.GetTotalMemory(false);
                    var memoryDelta = finalMemory - initialMemory;

                    // Record metrics for this list size
                    customMetrics[$"Size_{listSize}_SyncTime_ms"] = syncTimeMs;
                    customMetrics[$"Size_{listSize}_ItemsPerSecond"] = listSize / sw.Elapsed.TotalSeconds;
                    customMetrics[$"Size_{listSize}_MemoryDelta_bytes"] = memoryDelta;
                    customMetrics[$"Size_{listSize}_BytesPerItem"] = listSize > 0 ? memoryDelta / listSize : 0;

                    // Disable collection before disconnect
                    await clientList.DisableAsync();
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

                // Small delay between list size tests
                await Task.Delay(100, cancellationToken);
            }

            // Calculate aggregate statistics
            var avgSyncTimeMs = allSyncTimes.Count > 0 ? allSyncTimes.Average() : 0;

            return ScenarioResult.FromCollectionMeasurement(
                Name,
                Category,
                context.Transport,
                context.PayloadSize,
                new CollectionMetrics
                {
                    ItemCount = maxItemCount,
                    SyncTimeMs = avgSyncTimeMs,
                    TotalOperations = listSizes.Sum(),
                    Duration = TimeSpan.FromMilliseconds(allSyncTimes.Sum())
                },
                customMetrics);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
