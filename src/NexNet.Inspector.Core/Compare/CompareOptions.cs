namespace NexNet.Inspector.Core.Compare;

/// <summary>Settings of a compare.</summary>
public sealed record CompareOptions
{
    /// <summary>The defaults.</summary>
    public static CompareOptions Default { get; } = new();

    /// <summary>
    /// Aligned starts or durations that differ by no more than this are the same. Two captures of one connection differ
    /// by the network latency; two runs by scheduling.
    /// </summary>
    public TimeSpan TimingTolerance { get; init; } = TimeSpan.FromMilliseconds(2);

    /// <summary>Durations that differ by no more than this fraction of the longer one are the same.</summary>
    public double DurationTolerance { get; init; } = 0.25;

    /// <summary>
    /// The largest correlation table (entities of A times entities of B left after the common head and tail of a group)
    /// worked out exactly; beyond it the rest of the group is paired by position. Bounds the memory of the eager union
    /// on huge captures.
    /// </summary>
    public int MaxCorrelationCells { get; init; } = 1 << 20;

    /// <summary>Adds the capture gaps of both documents to the Δ list.</summary>
    public bool IncludeGaps { get; init; } = true;
}
