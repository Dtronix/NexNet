using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Quiescence;

/// <summary>
/// Aggregates per-session quiescence state across every session associated with a single
/// <c>NexusTestHost</c>. <see cref="QuiesceAsync"/> returns when every contributing counter is
/// zero, with an <c>await Task.Yield()</c> re-check so synchronously-scheduled continuations
/// observe their changes before quiescence is declared.
/// </summary>
/// <remarks>
/// The tracker exposes raw counter handles via <see cref="GetCountersFor"/> so the interceptor,
/// pipe factory, and transport can record activity directly. Pending-invocation probes are
/// pull-style because the session's invocation-state registry is the source of truth; counting
/// writes through the interceptor would double-count.
/// </remarks>
internal sealed class QuiescenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<long, QuiescenceCounters> _bySession = new();
    private readonly Dictionary<object, Func<int>> _pendingInvocationProbes = new();

    private TaskCompletionSource _changeSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QuiescenceCounters GetCountersFor(long sessionId)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var counters))
            {
                counters = new QuiescenceCounters();
                _bySession[sessionId] = counters;
            }
            return counters;
        }
    }

    /// <summary>
    /// Registers a probe under <paramref name="key"/> that returns the live pending-invocation
    /// count for a single session. Probes are aggregated into the global total each time
    /// <see cref="QuiesceAsync"/> samples the state. The <paramref name="key"/> is used to
    /// unregister the probe later (typically when the session disconnects or the host is
    /// disposed).
    /// </summary>
    public void RegisterPendingInvocationProbe(object key, Func<int> probe)
    {
        lock (_gate)
        {
            _pendingInvocationProbes[key] = probe;
        }
    }

    /// <summary>
    /// Removes a previously-registered probe.
    /// </summary>
    public void UnregisterPendingInvocationProbe(object key)
    {
        lock (_gate)
        {
            _pendingInvocationProbes.Remove(key);
        }
    }

    /// <summary>
    /// Wakes any caller currently awaiting quiescence. Called by the counter mutators after
    /// every change.
    /// </summary>
    public void SignalChange()
    {
        TaskCompletionSource toFire;
        lock (_gate)
        {
            toFire = _changeSignal;
            _changeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toFire.TrySetResult();
    }

    /// <summary>
    /// Atomic sample: reads counters and snapshots the signal task under the same lock so a
    /// caller awaiting the returned signal won't miss a <see cref="SignalChange"/> that happens
    /// after the sample but before the await.
    /// </summary>
    private (int bytesInTransit, int inDispatch, int activePipes, int pendingInvocations, Task signal) SampleAndSnapshotSignal()
    {
        int bytes = 0, dispatch = 0, pipes = 0, pending = 0;
        Task signal;
        lock (_gate)
        {
            foreach (var (_, counters) in _bySession)
            {
                bytes += counters.BytesInTransit;
                dispatch += counters.InDispatch;
                pipes += counters.ActivePipes;
            }
            foreach (var probe in _pendingInvocationProbes.Values)
            {
                try { pending += probe(); }
                catch { /* probe may be stale post-disconnect */ }
            }
            signal = _changeSignal.Task;
        }
        return (bytes, dispatch, pipes, pending, signal);
    }

    /// <summary>
    /// Waits until all counters are zero. Re-checks after a <see cref="Task.Yield"/> to drain
    /// synchronously-scheduled continuations that might enqueue further work without going
    /// through any quiescence-aware channel.
    /// </summary>
    public async Task QuiesceAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (bytes, dispatch, pipes, pending, signal) = SampleAndSnapshotSignal();
            if (bytes == 0 && dispatch == 0 && pipes == 0 && pending == 0)
            {
                // Drain synchronously-scheduled continuations.
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                (bytes, dispatch, pipes, pending, _) = SampleAndSnapshotSignal();
                if (bytes == 0 && dispatch == 0 && pipes == 0 && pending == 0)
                    return;
                // Re-sample to take a fresh signal for the next wait.
                (_, _, _, _, signal) = SampleAndSnapshotSignal();
            }

            if (cancellationToken.CanBeCanceled)
                signal = signal.WaitAsync(cancellationToken);

            await signal.ConfigureAwait(false);
        }
    }
}
