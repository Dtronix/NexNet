using NexNet.PerformanceBenchmarks.Config;

namespace NexNet.PerformanceBenchmarks.Infrastructure;

/// <summary>
/// Manages warmup iterations for benchmark scenarios.
/// </summary>
public sealed class WarmupManager
{
    private readonly BenchmarkSettings _settings;
    private readonly Action<string>? _logger;

    public WarmupManager(BenchmarkSettings settings, Action<string>? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Executes warmup iterations for a synchronous action.
    /// </summary>
    public void Warmup(Action action, string? description = null)
    {
        var desc = description ?? "operation";
        _logger?.Invoke($"Warming up {desc} ({_settings.WarmupIterations} iterations)...");

        for (int i = 0; i < _settings.WarmupIterations; i++)
        {
            action();
        }

        // Force GC after warmup to get clean state
        if (_settings.ForceGCBetweenIterations)
        {
            MetricsCollector.ForceGC();
        }
    }

    /// <summary>
    /// Executes warmup iterations for an async action.
    /// </summary>
    public async Task WarmupAsync(Func<Task> action, string? description = null)
    {
        var desc = description ?? "operation";
        _logger?.Invoke($"Warming up {desc} ({_settings.WarmupIterations} iterations)...");

        for (int i = 0; i < _settings.WarmupIterations; i++)
        {
            await action();
        }

        // Force GC after warmup to get clean state
        if (_settings.ForceGCBetweenIterations)
        {
            MetricsCollector.ForceGC();
        }
    }

    /// <summary>
    /// Executes warmup iterations for an async action with cancellation support.
    /// </summary>
    public async Task WarmupAsync(
        Func<CancellationToken, Task> action,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var desc = description ?? "operation";
        _logger?.Invoke($"Warming up {desc} ({_settings.WarmupIterations} iterations)...");

        for (int i = 0; i < _settings.WarmupIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action(cancellationToken);
        }

        // Force GC after warmup to get clean state
        if (_settings.ForceGCBetweenIterations)
        {
            MetricsCollector.ForceGC();
        }
    }

    /// <summary>
    /// Executes measured iterations and collects results.
    /// </summary>
    public List<double> Measure(Func<double> measuredAction, string? description = null)
    {
        var desc = description ?? "operation";
        _logger?.Invoke($"Measuring {desc} ({_settings.MeasuredIterations} iterations)...");

        var results = new List<double>(_settings.MeasuredIterations);

        for (int i = 0; i < _settings.MeasuredIterations; i++)
        {
            var measurement = measuredAction();
            results.Add(measurement);

            if (_settings.ForceGCBetweenIterations && i < _settings.MeasuredIterations - 1)
            {
                MetricsCollector.ForceGC();
            }
        }

        return results;
    }

    /// <summary>
    /// Executes measured async iterations and collects results.
    /// </summary>
    public async Task<List<double>> MeasureAsync(
        Func<Task<double>> measuredAction,
        string? description = null)
    {
        var desc = description ?? "operation";
        _logger?.Invoke($"Measuring {desc} ({_settings.MeasuredIterations} iterations)...");

        var results = new List<double>(_settings.MeasuredIterations);

        for (int i = 0; i < _settings.MeasuredIterations; i++)
        {
            var measurement = await measuredAction();
            results.Add(measurement);

            if (_settings.ForceGCBetweenIterations && i < _settings.MeasuredIterations - 1)
            {
                MetricsCollector.ForceGC();
            }
        }

        return results;
    }

    /// <summary>
    /// Executes measured async iterations with cancellation and collects results.
    /// </summary>
    public async Task<List<double>> MeasureAsync(
        Func<CancellationToken, Task<double>> measuredAction,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var desc = description ?? "operation";
        _logger?.Invoke($"Measuring {desc} ({_settings.MeasuredIterations} iterations)...");

        var results = new List<double>(_settings.MeasuredIterations);

        for (int i = 0; i < _settings.MeasuredIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var measurement = await measuredAction(cancellationToken);
            results.Add(measurement);

            if (_settings.ForceGCBetweenIterations && i < _settings.MeasuredIterations - 1)
            {
                MetricsCollector.ForceGC();
            }
        }

        return results;
    }

    /// <summary>
    /// Performs warmup then measurement in a single call.
    /// </summary>
    public async Task<List<double>> WarmupAndMeasureAsync(
        Func<Task<double>> action,
        string? description = null)
    {
        await WarmupAsync(async () => { await action(); }, description);
        return await MeasureAsync(action, description);
    }

    /// <summary>
    /// Stabilizes the environment before running benchmarks.
    /// </summary>
    public static void StabilizeEnvironment()
    {
        // Force full GC
        MetricsCollector.ForceGC();

        // Give the GC finalizer thread time to run
        Thread.Sleep(100);

        // Another GC pass to clean up any objects finalized
        MetricsCollector.ForceGC();
    }
}
