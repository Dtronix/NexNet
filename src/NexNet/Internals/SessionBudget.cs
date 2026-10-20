using System.Threading;

namespace NexNet.Internals;

/// <summary>
/// A session's receive memory budget (docs/internals/flow-control-v2.md, section 3). Counts reservations, not
/// allocations: credit granted to the peer and space promised for large values are reserved before anything is
/// sent or allocated, and released when they are no longer needed.
/// </summary>
internal sealed class SessionBudget
{
    private long _reserved;

    /// <summary>
    /// Times any budget was released below zero: more released than reserved. Always 0 unless reservations are
    /// miscounted; checked by the fuzz harnesses.
    /// </summary>
    internal static long InvariantViolations;

    public SessionBudget(long limit)
    {
        Limit = limit;
    }

    /// <summary>
    /// Total bytes that may be reserved at once.
    /// </summary>
    public long Limit { get; }

    /// <summary>
    /// Bytes currently reserved.
    /// </summary>
    public long Reserved => Volatile.Read(ref _reserved);

    /// <summary>
    /// Reserves <paramref name="bytes"/> if the budget can hold them, all or nothing.
    /// </summary>
    public bool TryReserve(long bytes)
    {
        var current = Volatile.Read(ref _reserved);
        while (true)
        {
            if (current + bytes > Limit)
                return false;

            var observed = Interlocked.CompareExchange(ref _reserved, current + bytes, current);
            if (observed == current)
                return true;
            current = observed;
        }
    }

    /// <summary>
    /// Returns <paramref name="bytes"/> reserved earlier.
    /// </summary>
    public void Release(long bytes)
    {
        if (Interlocked.Add(ref _reserved, -bytes) < 0)
            Interlocked.Increment(ref InvariantViolations);
    }
}
