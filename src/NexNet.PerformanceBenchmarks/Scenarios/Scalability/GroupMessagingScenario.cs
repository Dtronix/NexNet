using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;

namespace NexNet.PerformanceBenchmarks.Scenarios.Scalability;

/// <summary>
/// Measures group messaging performance with dynamic membership.
/// Tests group join/leave operations and targeted group broadcasts.
/// </summary>
public sealed class GroupMessagingScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "GroupMessaging";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Scalability;

    /// <inheritdoc />
    public override string Description => "Dynamic group membership and targeted group broadcasts";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => true;

    /// <summary>
    /// Uses Small payload for group messaging tests.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Number of clients to use in group tests.
    /// </summary>
    private const int ClientCount = 20;
    private const int QuickClientCount = 10;

    /// <summary>
    /// Number of groups to test with.
    /// </summary>
    private const int GroupCount = 4;

    /// <summary>
    /// Messages per group test.
    /// </summary>
    private const int MessagesPerGroup = 5;

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        // Create server
        var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
        await server.StartAsync(cancellationToken);

        try
        {
            var payload = context.GeneratePayload();
            var clientCount = context.Settings.MeasuredIterations <= 5 ? QuickClientCount : ClientCount;
            var customMetrics = new Dictionary<string, object>();

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

                // === Test 1: Group Join/Leave Performance ===
                var joinLatencies = new List<double>();
                var leaveLatencies = new List<double>();

                // Warmup
                await clients[0].client.Proxy.JoinGroup("warmup-group");
                await clients[0].client.Proxy.LeaveGroup("warmup-group");

                // Measure join latencies
                for (int i = 0; i < clientCount; i++)
                {
                    var groupName = $"group-{i % GroupCount}";
                    var sw = Stopwatch.StartNew();
                    await clients[i].client.Proxy.JoinGroup(groupName);
                    sw.Stop();
                    joinLatencies.Add(sw.Elapsed.TotalMicroseconds);
                }

                // Measure leave latencies
                for (int i = 0; i < clientCount; i++)
                {
                    var groupName = $"group-{i % GroupCount}";
                    var sw = Stopwatch.StartNew();
                    await clients[i].client.Proxy.LeaveGroup(groupName);
                    sw.Stop();
                    leaveLatencies.Add(sw.Elapsed.TotalMicroseconds);
                }

                var joinStats = StatisticsCalculator.Calculate(joinLatencies);
                var leaveStats = StatisticsCalculator.Calculate(leaveLatencies);

                customMetrics["JoinGroup_MeanLatency_us"] = joinStats.Mean;
                customMetrics["JoinGroup_P95Latency_us"] = joinStats.P95;
                customMetrics["LeaveGroup_MeanLatency_us"] = leaveStats.Mean;
                customMetrics["LeaveGroup_P95Latency_us"] = leaveStats.P95;

                // === Test 2: Targeted Group Messaging ===
                // Re-join groups with balanced distribution
                for (int i = 0; i < clientCount; i++)
                {
                    var groupName = $"group-{i % GroupCount}";
                    await clients[i].client.Proxy.JoinGroup(groupName);
                }

                // Reset metrics
                foreach (var (nexus, _) in clients)
                {
                    nexus.ResetMetrics();
                }

                var groupMessageLatencies = new List<double>();
                var deliverySpreads = new List<double>();

                // Send messages to each group
                for (int g = 0; g < GroupCount; g++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var groupName = $"group-{g}";
                    var groupClients = clients.Where((c, i) => i % GroupCount == g).ToList();

                    for (int msg = 0; msg < MessagesPerGroup; msg++)
                    {
                        // Reset metrics for group members
                        foreach (var (nexus, _) in groupClients)
                        {
                            nexus.ResetMetrics();
                        }

                        // Send group message
                        var sw = Stopwatch.StartNew();
                        await clients[0].client.Proxy.RequestGroupMessage(groupName, payload);

                        // Wait for all group members to receive
                        try
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            cts.CancelAfter(TimeSpan.FromSeconds(5));

                            var receiveTasks = groupClients.Select(c =>
                                WaitForGroupMessageAsync(c.nexus, groupName, cts.Token));
                            await Task.WhenAll(receiveTasks);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            // Timeout waiting for group messages
                        }

                        sw.Stop();
                        groupMessageLatencies.Add(sw.Elapsed.TotalMicroseconds);

                        // Calculate delivery spread for group
                        long firstTimestamp = long.MaxValue;
                        long lastTimestamp = long.MinValue;

                        foreach (var (nexus, _) in groupClients)
                        {
                            // Use group message count as indicator
                            if (nexus.GroupMessageCount > 0)
                            {
                                var count = nexus.GroupMessageCounts.TryGetValue(groupName, out var c) ? c : 0;
                                if (count > 0)
                                {
                                    // Approximate timestamps
                                    var now = Stopwatch.GetTimestamp();
                                    if (firstTimestamp == long.MaxValue)
                                        firstTimestamp = now;
                                    lastTimestamp = now;
                                }
                            }
                        }

                        if (firstTimestamp != long.MaxValue && lastTimestamp != long.MinValue)
                        {
                            var spreadTicks = lastTimestamp - firstTimestamp;
                            var spreadMicroseconds = (double)spreadTicks / Stopwatch.Frequency * 1_000_000;
                            deliverySpreads.Add(spreadMicroseconds);
                        }

                        await Task.Delay(10, cancellationToken);
                    }
                }

                // Calculate group message stats
                if (groupMessageLatencies.Count > 0)
                {
                    var msgStats = StatisticsCalculator.Calculate(groupMessageLatencies);
                    customMetrics["GroupMessage_MeanLatency_us"] = msgStats.Mean;
                    customMetrics["GroupMessage_P95Latency_us"] = msgStats.P95;
                    customMetrics["GroupMessage_Count"] = groupMessageLatencies.Count;
                }

                // === Test 3: Dynamic Membership Churn ===
                // Rapidly join/leave groups while messages are being sent
                var churnOperations = 0;
                var churnErrors = 0;
                var churnSw = Stopwatch.StartNew();
                var churnDuration = TimeSpan.FromSeconds(context.Settings.MeasuredIterations <= 5 ? 1 : 2);

                using (var churnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    churnCts.CancelAfter(churnDuration);

                    try
                    {
                        while (!churnCts.IsCancellationRequested)
                        {
                            var clientIndex = churnOperations % clientCount;
                            var groupIndex = (churnOperations / clientCount) % GroupCount;
                            var groupName = $"churn-group-{groupIndex}";

                            try
                            {
                                // Join and immediately leave
                                await clients[clientIndex].client.Proxy.JoinGroup(groupName);
                                await clients[clientIndex].client.Proxy.LeaveGroup(groupName);
                                churnOperations += 2; // Count both join and leave
                            }
                            catch
                            {
                                churnErrors++;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Expected timeout
                    }
                }

                churnSw.Stop();

                var churnOpsPerSec = churnOperations / churnSw.Elapsed.TotalSeconds;
                customMetrics["GroupChurn_OpsPerSec"] = churnOpsPerSec;
                customMetrics["GroupChurn_TotalOps"] = churnOperations;
                customMetrics["GroupChurn_Errors"] = churnErrors;

                // Combine all latency measurements
                var allLatencies = joinLatencies.Concat(leaveLatencies).Concat(groupMessageLatencies).ToList();

                return ScenarioResult.FromScalabilityMeasurement(
                    Name,
                    Category,
                    context.Transport,
                    context.PayloadSize,
                    clientCount,
                    allLatencies,
                    customMetrics: customMetrics);
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
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task WaitForGroupMessageAsync(BenchmarkClientNexus nexus, string groupName, CancellationToken cancellationToken)
    {
        // Simple polling with short delay
        while (!cancellationToken.IsCancellationRequested)
        {
            if (nexus.GroupMessageCounts.TryGetValue(groupName, out var count) && count > 0)
                return;

            await Task.Delay(1, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
