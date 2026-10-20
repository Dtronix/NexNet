using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Internals;

/// <summary>
/// A session's receive memory budget (docs/internals/flow-control.md, section 3). Counts reservations, not
/// allocations: credit granted to the peer and space promised for large values are reserved before anything is
/// sent or allocated, and released when they are no longer needed.
/// </summary>
internal sealed class SessionBudget
{
    private long _reserved;

    // Reservations waiting for room, served in order (ReserveAsync).
    private readonly Queue<(long Bytes, TaskCompletionSource<bool> Waiter)> _waiters = new();
    private readonly Lock _waitersLock = new();

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
    /// Reserves <paramref name="bytes"/>, all or nothing, waiting in FIFO order behind earlier waiters until the budget
    /// has room. Returns false if the wait is canceled or the request can never fit.
    /// </summary>
    public async ValueTask<bool> ReserveAsync(long bytes, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> waiter;
        lock (_waitersLock)
        {
            if (_waiters.Count == 0 && TryReserve(bytes))
                return true;

            if (bytes > Limit)
                return false;

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue((bytes, waiter));
        }

        var registration = cancellationToken.Register(static w => ((TaskCompletionSource<bool>)w!).TrySetResult(false), waiter);
        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns <paramref name="bytes"/> reserved earlier, then serves waiting reservations that now fit.
    /// </summary>
    public void Release(long bytes)
    {
        if (Interlocked.Add(ref _reserved, -bytes) < 0)
            Interlocked.Increment(ref InvariantViolations);

        lock (_waitersLock)
        {
            while (_waiters.TryPeek(out var next))
            {
                // A canceled waiter gave up its place.
                if (next.Waiter.Task.IsCompleted)
                {
                    _waiters.Dequeue();
                    continue;
                }

                if (!TryReserve(next.Bytes))
                    break;

                _waiters.Dequeue();
                if (!next.Waiter.TrySetResult(true))
                    Interlocked.Add(ref _reserved, -next.Bytes); // canceled meanwhile: give the bytes back
            }
        }
    }
}
