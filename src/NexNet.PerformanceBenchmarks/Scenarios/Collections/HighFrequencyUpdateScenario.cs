using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Collections;

/// <summary>
/// Measures high-frequency update performance for synchronized collections.
/// Tests how fast operations can be performed and synced to clients.
/// </summary>
public sealed class HighFrequencyUpdateScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "HighFrequencyUpdate";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Collections;

    /// <inheritdoc />
    public override string Description => "High-frequency collection updates measuring operations per second";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// Uses Small payload for update test.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Duration for full update test.
    /// </summary>
    private static readonly TimeSpan FullDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Duration for quick update test.
    /// </summary>
    private static readonly TimeSpan QuickDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Initial items to populate before update test.
    /// </summary>
    private const int InitialItems = 100;

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var duration = context.Settings.MeasuredIterations <= 5 ? QuickDuration : FullDuration;

            // Populate server-side collection with initial items
            using var owner = server.ContextProvider.Rent();
            var serverList = owner.Collections.SyncList;

            await serverList.ClearAsync();
            for (int i = 0; i < InitialItems; i++)
            {
                await serverList.AddAsync(TestItem.Create(i));
            }

            // Create and connect client
            var clientNexus = new BenchmarkClientNexus();
            var client = BenchmarkClientNexus.CreateClient(context.ClientConfig, clientNexus);
            await client.ConnectAsync(cancellationToken);

            try
            {
                // Get client-side collection and sync
                var clientList = client.Proxy.SyncList;
                await clientList.EnableAsync(cancellationToken);
                await clientList.ReadyTask.WaitAsync(cancellationToken);

                // Verify initial sync
                if (clientList.Count != InitialItems)
                {
                    return ScenarioResult.Failed(
                        Name, Category, context.Transport, context.PayloadSize,
                        $"Initial sync failed: expected {InitialItems}, got {clientList.Count}");
                }

                // Record initial memory state
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var initialMemory = GC.GetTotalMemory(false);
                var initialGen0 = GC.CollectionCount(0);
                var initialGen1 = GC.CollectionCount(1);
                var initialGen2 = GC.CollectionCount(2);

                // Operation counters
                long addCount = 0, insertCount = 0, removeCount = 0;
                long removeAtCount = 0, replaceCount = 0, moveCount = 0, clearCount = 0;
                var errors = 0;
                var latencies = new List<double>();
                var random = new Random(42);
                var itemId = InitialItems;

                using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                durationCts.CancelAfter(duration);

                var totalSw = Stopwatch.StartNew();

                try
                {
                    while (!durationCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var currentCount = serverList.Count;
                            var operation = random.Next(7);
                            var opSw = Stopwatch.StartNew();

                            switch (operation)
                            {
                                case 0: // Add
                                    await serverList.AddAsync(TestItem.Create(itemId++));
                                    addCount++;
                                    break;

                                case 1: // Insert (if list has items)
                                    if (currentCount > 0)
                                    {
                                        var insertIndex = random.Next(currentCount);
                                        await serverList.InsertAsync(insertIndex, TestItem.Create(itemId++));
                                        insertCount++;
                                    }
                                    break;

                                case 2: // Remove (if list has items)
                                    if (currentCount > 0)
                                    {
                                        var removeIndex = random.Next(currentCount);
                                        var itemToRemove = serverList[removeIndex];
                                        await serverList.RemoveAsync(itemToRemove);
                                        removeCount++;
                                    }
                                    break;

                                case 3: // RemoveAt (if list has items)
                                    if (currentCount > 0)
                                    {
                                        var removeAtIndex = random.Next(currentCount);
                                        await serverList.RemoveAtAsync(removeAtIndex);
                                        removeAtCount++;
                                    }
                                    break;

                                case 4: // Replace (if list has items)
                                    if (currentCount > 0)
                                    {
                                        var replaceIndex = random.Next(currentCount);
                                        await serverList.ReplaceAsync(replaceIndex, TestItem.Create(itemId++));
                                        replaceCount++;
                                    }
                                    break;

                                case 5: // Move (if list has at least 2 items)
                                    if (currentCount >= 2)
                                    {
                                        var fromIndex = random.Next(currentCount);
                                        var toIndex = random.Next(currentCount);
                                        if (fromIndex != toIndex)
                                        {
                                            await serverList.MoveAsync(fromIndex, toIndex);
                                            moveCount++;
                                        }
                                    }
                                    break;

                                case 6: // Clear (occasionally, then repopulate)
                                    if (random.Next(100) < 5) // 5% chance
                                    {
                                        await serverList.ClearAsync();
                                        clearCount++;
                                        // Repopulate with a few items
                                        for (int i = 0; i < 10; i++)
                                        {
                                            await serverList.AddAsync(TestItem.Create(itemId++));
                                            addCount++;
                                        }
                                    }
                                    break;
                            }

                            opSw.Stop();
                            latencies.Add(opSw.Elapsed.TotalMicroseconds);
                        }
                        catch (OperationCanceledException) when (durationCts.Token.IsCancellationRequested)
                        {
                            break;
                        }
                        catch
                        {
                            errors++;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Expected timeout
                }

                totalSw.Stop();

                // Capture final metrics
                var finalMemory = GC.GetTotalMemory(false);
                var finalGen0 = GC.CollectionCount(0) - initialGen0;
                var finalGen1 = GC.CollectionCount(1) - initialGen1;
                var finalGen2 = GC.CollectionCount(2) - initialGen2;

                var totalOperations = addCount + insertCount + removeCount + removeAtCount + replaceCount + moveCount + clearCount;
                var opsPerSecond = totalOperations / totalSw.Elapsed.TotalSeconds;

                LatencyMetrics? updateLatency = null;
                if (latencies.Count > 0)
                {
                    var stats = StatisticsCalculator.Calculate(latencies);
                    updateLatency = new LatencyMetrics
                    {
                        MeanMicroseconds = stats.Mean,
                        StdDevMicroseconds = stats.StdDev,
                        MinMicroseconds = stats.Min,
                        MaxMicroseconds = stats.Max,
                        P50Microseconds = stats.P50,
                        P95Microseconds = stats.P95,
                        P99Microseconds = stats.P99,
                        IterationCount = latencies.Count
                    };
                }

                var customMetrics = new Dictionary<string, object>
                {
                    ["AddCount"] = addCount,
                    ["InsertCount"] = insertCount,
                    ["RemoveCount"] = removeCount,
                    ["RemoveAtCount"] = removeAtCount,
                    ["ReplaceCount"] = replaceCount,
                    ["MoveCount"] = moveCount,
                    ["ClearCount"] = clearCount,
                    ["TotalOperations"] = totalOperations,
                    ["OpsPerSecond"] = opsPerSecond,
                    ["Duration_seconds"] = totalSw.Elapsed.TotalSeconds,
                    ["Errors"] = errors,
                    ["FinalListCount"] = serverList.Count
                };

                // Disable collection before disconnect
                await clientList.DisableAsync();

                return ScenarioResult.FromCollectionMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    new CollectionMetrics
                    {
                        ItemCount = serverList.Count,
                        OperationsPerSecond = opsPerSecond,
                        TotalOperations = totalOperations,
                        Duration = totalSw.Elapsed,
                        UpdateLatency = updateLatency,
                        MemoryMetrics = new MemoryMetrics
                        {
                            AllocatedBytes = finalMemory - initialMemory,
                            Gen0Collections = finalGen0,
                            Gen1Collections = finalGen1,
                            Gen2Collections = finalGen2
                        },
                        ErrorCount = errors
                    },
                    customMetrics);
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
