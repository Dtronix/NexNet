using NexNet.PerformanceBenchmarks.Config;

namespace NexNet.PerformanceBenchmarks.Scenarios;

/// <summary>
/// Base interface for all benchmark scenarios.
/// </summary>
public interface IScenario
{
    /// <summary>
    /// The unique name of the scenario.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The category this scenario belongs to.
    /// </summary>
    BenchmarkCategory Category { get; }

    /// <summary>
    /// Description of what this scenario measures.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The payload sizes this scenario supports.
    /// </summary>
    IReadOnlyList<PayloadSize> SupportedPayloads { get; }

    /// <summary>
    /// Whether this scenario requires multiple clients.
    /// </summary>
    bool RequiresMultipleClients { get; }

    /// <summary>
    /// Runs the benchmark scenario and returns the results.
    /// </summary>
    /// <param name="context">The execution context with configuration and resources.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scenario results.</returns>
    Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base class for benchmark scenarios providing common functionality.
/// </summary>
public abstract class ScenarioBase : IScenario
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract BenchmarkCategory Category { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public virtual IReadOnlyList<PayloadSize> SupportedPayloads { get; } =
        [PayloadSize.Tiny, PayloadSize.Small, PayloadSize.Medium, PayloadSize.Large, PayloadSize.XLarge];

    /// <inheritdoc />
    public virtual bool RequiresMultipleClients => false;

    /// <inheritdoc />
    public abstract Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the display name for the scenario including transport and payload info.
    /// </summary>
    public string GetDisplayName(TransportType transport, PayloadSize? payload = null)
    {
        return payload.HasValue
            ? $"{Name}/{transport}/{payload}"
            : $"{Name}/{transport}";
    }
}
