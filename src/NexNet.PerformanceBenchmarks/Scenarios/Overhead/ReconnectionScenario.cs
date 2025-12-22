using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Infrastructure;
using NexNet.PerformanceBenchmarks.Nexuses;
using NexNet.Transports;

namespace NexNet.PerformanceBenchmarks.Scenarios.Overhead;

/// <summary>
/// Measures reconnection time after server-initiated disconnect.
/// Tests the full reconnection cycle: detect disconnect, attempt reconnection, restore state.
/// </summary>
public sealed class ReconnectionScenario : ScenarioBase
{
    /// <inheritdoc />
    public override string Name => "Reconnection";

    /// <inheritdoc />
    public override BenchmarkCategory Category => BenchmarkCategory.Overhead;

    /// <inheritdoc />
    public override string Description => "Client reconnection time after server restart";

    /// <inheritdoc />
    public override bool RequiresMultipleClients => false;

    /// <summary>
    /// No payload needed for reconnection testing.
    /// </summary>
    public override IReadOnlyList<PayloadSize> SupportedPayloads { get; } = [PayloadSize.Small];

    /// <summary>
    /// Number of full reconnection cycles.
    /// </summary>
    private const int FullReconnectionCycles = 10;

    /// <summary>
    /// Number of quick reconnection cycles.
    /// </summary>
    private const int QuickReconnectionCycles = 3;

    /// <summary>
    /// Maximum time to wait for reconnection.
    /// </summary>
    private static readonly TimeSpan ReconnectionTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public override async Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var cycles = context.Settings.MeasuredIterations <= 5 ? QuickReconnectionCycles : FullReconnectionCycles;
        var reconnectionTimes = new List<double>();
        var disconnectDetectionTimes = new List<double>();
        var successfulReconnections = 0;
        var failedReconnections = 0;

        // Configure client with fast reconnection policy
        var clientConfig = context.ClientConfig;
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(
            [
                TimeSpan.Zero,              // Immediate first attempt
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1)
            ],
            continuousRetry: false
        );

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Create fresh server for each cycle
            var server = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
            await server.StartAsync(cancellationToken);

            try
            {
                // Create and connect client
                var clientNexus = new BenchmarkClientNexus();
                clientNexus.ResetMetrics();
                var client = BenchmarkClientNexus.CreateClient(clientConfig, clientNexus);

                var reconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var disconnectTime = Stopwatch.GetTimestamp();
                var reconnectStartTime = 0L;

                // Track state changes
                client.StateChanged += (_, state) =>
                {
                    if (state == ConnectionState.Disconnected)
                    {
                        disconnectedTcs.TrySetResult(true);
                    }
                    else if (state == ConnectionState.Reconnecting)
                    {
                        reconnectStartTime = Stopwatch.GetTimestamp();
                    }
                    else if (state == ConnectionState.Connected)
                    {
                        if (reconnectStartTime > 0)
                        {
                            reconnectedTcs.TrySetResult(true);
                        }
                    }
                };

                await client.ConnectAsync(cancellationToken);

                try
                {
                    // Verify connection works
                    await client.Proxy.Ping();

                    // Record disconnect start time
                    disconnectTime = Stopwatch.GetTimestamp();

                    // Stop server to force disconnect
                    await server.StopAsync();

                    // Wait for disconnect detection
                    try
                    {
                        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                        var detectTime = Stopwatch.GetTimestamp();
                        var detectDuration = (detectTime - disconnectTime) * 1000.0 / Stopwatch.Frequency;
                        disconnectDetectionTimes.Add(detectDuration);
                    }
                    catch (TimeoutException)
                    {
                        // Disconnect not detected in time
                    }

                    // Restart server
                    var newServer = BenchmarkServerNexus.CreateServer(context.ServerConfig, () => new BenchmarkServerNexus());
                    await newServer.StartAsync(cancellationToken);

                    try
                    {
                        var reconnectStart = Stopwatch.GetTimestamp();

                        // Wait for reconnection
                        try
                        {
                            await reconnectedTcs.Task.WaitAsync(ReconnectionTimeout, cancellationToken);
                            var reconnectEnd = Stopwatch.GetTimestamp();
                            var reconnectDuration = (reconnectEnd - reconnectStart) * 1000.0 / Stopwatch.Frequency;
                            reconnectionTimes.Add(reconnectDuration);
                            successfulReconnections++;

                            // Verify connection works after reconnection
                            await client.Proxy.Ping();
                        }
                        catch (TimeoutException)
                        {
                            failedReconnections++;
                        }
                    }
                    finally
                    {
                        await newServer.StopAsync();
                    }
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
                try
                {
                    await server.StopAsync();
                }
                catch
                {
                    // Server may already be stopped
                }
            }

            // Small delay between cycles
            await Task.Delay(100, cancellationToken);
        }

        // Calculate statistics
        LatencyMetrics? reconnectLatency = null;
        if (reconnectionTimes.Count > 0)
        {
            var stats = StatisticsCalculator.Calculate(reconnectionTimes);
            reconnectLatency = new LatencyMetrics
            {
                MeanMicroseconds = stats.Mean * 1000, // Convert ms to us
                StdDevMicroseconds = stats.StdDev * 1000,
                MinMicroseconds = stats.Min * 1000,
                MaxMicroseconds = stats.Max * 1000,
                P50Microseconds = stats.P50 * 1000,
                P95Microseconds = stats.P95 * 1000,
                P99Microseconds = stats.P99 * 1000,
                IterationCount = reconnectionTimes.Count
            };
        }

        LatencyMetrics? detectLatency = null;
        if (disconnectDetectionTimes.Count > 0)
        {
            var stats = StatisticsCalculator.Calculate(disconnectDetectionTimes);
            detectLatency = new LatencyMetrics
            {
                MeanMicroseconds = stats.Mean * 1000, // Convert ms to us
                StdDevMicroseconds = stats.StdDev * 1000,
                MinMicroseconds = stats.Min * 1000,
                MaxMicroseconds = stats.Max * 1000,
                P50Microseconds = stats.P50 * 1000,
                P95Microseconds = stats.P95 * 1000,
                P99Microseconds = stats.P99 * 1000,
                IterationCount = disconnectDetectionTimes.Count
            };
        }

        var customMetrics = new Dictionary<string, object>
        {
            ["TotalCycles"] = cycles,
            ["SuccessfulReconnections"] = successfulReconnections,
            ["FailedReconnections"] = failedReconnections,
            ["SuccessRate_Percent"] = cycles > 0 ? (successfulReconnections * 100.0 / cycles) : 0,
            ["Reconnect_Mean_ms"] = reconnectionTimes.Count > 0 ? reconnectionTimes.Average() : 0,
            ["Reconnect_Min_ms"] = reconnectionTimes.Count > 0 ? reconnectionTimes.Min() : 0,
            ["Reconnect_Max_ms"] = reconnectionTimes.Count > 0 ? reconnectionTimes.Max() : 0,
            ["DetectDisconnect_Mean_ms"] = disconnectDetectionTimes.Count > 0 ? disconnectDetectionTimes.Average() : 0
        };

        return ScenarioResult.FromStressMeasurement(
            Name,
            Category,
            context.Transport,
            context.PayloadSize,
            new StressMetrics
            {
                TotalOperations = cycles,
                OperationsPerSecond = 0, // Not applicable
                Duration = TimeSpan.Zero, // Not a duration-based test
                ErrorCount = failedReconnections,
                ConnectLatency = reconnectLatency,
                DisconnectLatency = detectLatency
            },
            customMetrics);
    }
}
