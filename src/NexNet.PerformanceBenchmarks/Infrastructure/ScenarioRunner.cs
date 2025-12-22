using System.Diagnostics;
using NexNet.PerformanceBenchmarks.Config;
using NexNet.PerformanceBenchmarks.Scenarios;

namespace NexNet.PerformanceBenchmarks.Infrastructure;

/// <summary>
/// Orchestrates the execution of benchmark scenarios.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly BenchmarkSettings _settings;
    private readonly CliOptions _options;
    private readonly List<IScenario> _scenarios = [];
    private readonly Action<string> _logger;

    public ScenarioRunner(BenchmarkSettings settings, CliOptions options, Action<string>? logger = null)
    {
        _settings = settings;
        _options = options;
        _logger = logger ?? Console.WriteLine;
    }

    /// <summary>
    /// Registers a scenario to be run.
    /// </summary>
    public void RegisterScenario(IScenario scenario)
    {
        _scenarios.Add(scenario);
    }

    /// <summary>
    /// Registers multiple scenarios.
    /// </summary>
    public void RegisterScenarios(IEnumerable<IScenario> scenarios)
    {
        _scenarios.AddRange(scenarios);
    }

    /// <summary>
    /// Gets the list of registered scenarios.
    /// </summary>
    public IReadOnlyList<IScenario> Scenarios => _scenarios;

    /// <summary>
    /// Filters scenarios based on CLI options.
    /// </summary>
    public IEnumerable<IScenario> GetFilteredScenarios()
    {
        IEnumerable<IScenario> filtered = _scenarios;

        // Filter by category
        if (!string.IsNullOrEmpty(_options.Category) &&
            Enum.TryParse<BenchmarkCategory>(_options.Category, ignoreCase: true, out var category))
        {
            filtered = filtered.Where(s => s.Category == category);
        }

        // Filter by scenario name
        if (!string.IsNullOrEmpty(_options.Scenario))
        {
            filtered = filtered.Where(s =>
                s.Name.Equals(_options.Scenario, StringComparison.OrdinalIgnoreCase));
        }

        return filtered;
    }

    /// <summary>
    /// Runs all filtered scenarios across all configured transports and payloads.
    /// </summary>
    public async Task<BenchmarkRun> RunAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var overallStopwatch = Stopwatch.StartNew();
        var results = new List<ScenarioResult>();

        var scenarios = GetFilteredScenarios().ToList();
        var transports = _options.GetTransportTypes();
        var payloads = _options.GetPayloadSizes();

        _logger($"Starting benchmark run at {startTime:O}");
        _logger($"Scenarios: {scenarios.Count}, Transports: {transports.Count}, Payloads: {payloads.Count}");
        _logger("");

        // Pre-run stabilization
        WarmupManager.StabilizeEnvironment();

        foreach (var scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger($"=== Scenario: {scenario.Name} ({scenario.Category}) ===");
            _logger($"    {scenario.Description}");

            foreach (var transport in transports)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TransportFactory.IsTransportAvailable(transport))
                {
                    _logger($"  [{transport}] SKIPPED: {TransportFactory.GetUnavailableReason(transport)}");
                    continue;
                }

                // Determine which payloads to test for this scenario
                var applicablePayloads = scenario.SupportedPayloads
                    .Where(p => payloads.Contains(p))
                    .ToList();

                if (applicablePayloads.Count == 0)
                {
                    // No payload filtering for this scenario, run once
                    var result = await RunSingleScenarioAsync(scenario, transport, null, cancellationToken);
                    results.Add(result);
                }
                else
                {
                    foreach (var payload in applicablePayloads)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = await RunSingleScenarioAsync(scenario, transport, payload, cancellationToken);
                        results.Add(result);
                    }
                }

                // Force GC between transports
                if (_settings.ForceGCBetweenIterations)
                {
                    MetricsCollector.ForceGC();
                }
            }

            _logger("");
        }

        overallStopwatch.Stop();

        _logger($"Benchmark run completed in {overallStopwatch.Elapsed:g}");
        _logger($"Total results: {results.Count}");
        _logger($"Successful: {results.Count(r => r.Success)}, Failed: {results.Count(r => !r.Success)}");

        return new BenchmarkRun
        {
            StartTime = startTime,
            Duration = overallStopwatch.Elapsed,
            Settings = _settings,
            Results = results
        };
    }

    /// <summary>
    /// Runs a single scenario with the given configuration.
    /// </summary>
    private async Task<ScenarioResult> RunSingleScenarioAsync(
        IScenario scenario,
        TransportType transport,
        PayloadSize? payload,
        CancellationToken cancellationToken)
    {
        var displayName = payload.HasValue
            ? $"  [{transport}/{payload}]"
            : $"  [{transport}]";

        _logger($"{displayName} Running...");

        try
        {
            var (serverConfig, clientConfig) = TransportFactory.CreateConfigs(transport);

            using var context = new ScenarioContext(
                transport,
                payload ?? PayloadSize.Small,
                _settings,
                serverConfig,
                clientConfig);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_settings.IterationTimeout);

            var result = await scenario.RunAsync(context, cts.Token);

            var statusIcon = result.Success ? "OK" : "FAIL";
            var summary = GetResultSummary(result);
            _logger($"{displayName} {statusIcon} - {summary}");

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Re-throw if user cancelled
        }
        catch (Exception ex)
        {
            _logger($"{displayName} ERROR: {ex.Message}");

            return ScenarioResult.Failed(
                scenario.Name,
                scenario.Category,
                transport,
                payload,
                ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Gets a brief summary string for a result.
    /// </summary>
    private static string GetResultSummary(ScenarioResult result)
    {
        if (!result.Success)
            return result.ErrorMessage ?? "Failed";

        if (result.LatencyMetrics != null)
        {
            var m = result.LatencyMetrics;
            return $"P50={m.P50Microseconds:F1}us P95={m.P95Microseconds:F1}us P99={m.P99Microseconds:F1}us";
        }

        if (result.ThroughputMetrics != null)
        {
            var m = result.ThroughputMetrics;
            if (m.MegabytesPerSecond > 0)
                return $"{m.MegabytesPerSecond:F2} MB/s";
            if (m.OperationsPerSecond > 0)
                return $"{m.OperationsPerSecond:F0} ops/s";
            if (m.MessagesPerSecond > 0)
                return $"{m.MessagesPerSecond:F0} msg/s";
        }

        if (result.ScalabilityMetrics != null)
        {
            var m = result.ScalabilityMetrics;
            return $"Clients={m.ClientCount} Spread={m.DeliverySpreadMicroseconds:F1}us";
        }

        return "Completed";
    }

    /// <summary>
    /// Lists all registered scenarios without running them.
    /// </summary>
    public void ListScenarios()
    {
        _logger("Registered Scenarios:");
        _logger("");

        var byCategory = _scenarios.GroupBy(s => s.Category);

        foreach (var group in byCategory.OrderBy(g => g.Key))
        {
            _logger($"  {group.Key}:");
            foreach (var scenario in group.OrderBy(s => s.Name))
            {
                var payloads = scenario.SupportedPayloads.Any()
                    ? string.Join(",", scenario.SupportedPayloads.Select(p => p.ToString()))
                    : "N/A";
                _logger($"    - {scenario.Name}");
                _logger($"      {scenario.Description}");
                _logger($"      Payloads: {payloads}");
            }
            _logger("");
        }
    }
}

/// <summary>
/// Represents a complete benchmark run with all results.
/// </summary>
public sealed record BenchmarkRun
{
    public DateTime StartTime { get; init; }
    public TimeSpan Duration { get; init; }
    public required BenchmarkSettings Settings { get; init; }
    public required List<ScenarioResult> Results { get; init; }

    public string? CommitHash { get; init; }
    public string? CommitMessage { get; init; }
    public string? Branch { get; init; }

    public string RuntimeVersion => Environment.Version.ToString();
    public string OSDescription => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}
