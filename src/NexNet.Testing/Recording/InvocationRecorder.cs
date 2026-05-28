using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Recording;

/// <summary>
/// Append-only, snapshot-readable log of invocations seen on a single session. Writers (the
/// session's interceptor) take the internal lock for the duration of the append; readers
/// (assertions) take it briefly to snapshot the current list and walk a stable copy. The
/// signal exposed via <see cref="WaitForChangeAsync"/> wakes <c>WaitFor</c> when a new record
/// arrives.
/// </summary>
internal sealed class InvocationRecorder
{
    private readonly object _gate = new();
    private readonly List<InvocationRecord> _records = new();
    private TaskCompletionSource _changeSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Appends a record and signals any waiters that the recorder has changed. Returns the
    /// 1-based index of the inserted record (purely informational).
    /// </summary>
    public int Append(InvocationRecord record)
    {
        TaskCompletionSource? signalToFire;
        int index;
        lock (_gate)
        {
            _records.Add(record);
            index = _records.Count;
            signalToFire = _changeSignal;
            _changeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signalToFire.TrySetResult();
        return index;
    }

    /// <summary>Snapshot of the currently-recorded invocations. Safe to enumerate.</summary>
    public IReadOnlyList<InvocationRecord> Snapshot()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    /// <summary>
    /// Returns a task that completes the next time a record is appended. Designed for
    /// long-poll style waiters; callers should re-check the snapshot after the task completes
    /// because multiple concurrent waiters all wake on the same signal.
    /// </summary>
    public Task WaitForChangeAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            signal = _changeSignal;
        }

        if (!cancellationToken.CanBeCanceled)
            return signal.Task;

        return signal.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Current record count. Cheap; takes the lock for a single read.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }
}
