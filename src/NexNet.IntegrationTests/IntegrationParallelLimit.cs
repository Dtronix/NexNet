using TUnit.Core.Interfaces;

namespace NexNet.IntegrationTests;

/// <summary>
/// How many integration tests run at once: half the logical processors, at least two. Set
/// <c>NEXNET_TEST_PARALLEL</c> to a positive number to override it (1 runs the suite serially).
/// </summary>
public sealed class IntegrationParallelLimit : IParallelLimit
{
    public int Limit { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("NEXNET_TEST_PARALLEL"), out var limit) && limit > 0
            ? limit
            : Math.Max(2, Environment.ProcessorCount / 2);
}
