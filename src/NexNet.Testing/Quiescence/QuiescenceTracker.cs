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
/// pipe factory, and transport can record activity directly. <see cref="GetPendingInvocations"/>
/// is a pull-style probe used because the session's invocation-state registry is the source of
/// truth — counting writes through the interceptor would double-count.
/// </remarks>
internal sealed class QuiescenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<long, QuiescenceCounters> _bySession = new();
    private readonly List<Func<int>> _pendingInvocationProbes = new();

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
    /// Registers a probe that returns the live pending-invocation count for a single session.
    /// Probes are aggregated into the global total each time <see cref="QuiesceAsync"/> samples
    /// the state.
    /// </summary>
    public void RegisterPendingInvocationProbe(Func<int> probe)
    {
        lock (_gate)
        {
            _pendingInvocationProbes.Add(probe);
        }
    }

    /// <summary>
    /// Removes a previously-registered probe (typically called when a session disconnects).
    /// </summary>
    public void UnregisterPendingInvocationProbe(Func<int> probe)
    {
        lock (_gate)
        {
            _pendingInvocationProbes.Remove(probe);
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

    private (int bytesInTransit, int inDispatch, int activePipes, int pendingInvocations) Sample()
    {
        int bytes = 0, dispatch = 0, pipes = 0, pending = 0;
        lock (_gate)
        {
            foreach (var (_, counters) in _bySession)
            {
                bytes += counters.BytesInTransit;
                dispatch += counters.InDispatch;
                pipes += counters.ActivePipes;
            }
            foreach (var probe in _pendingInvocationProbes)
            {
                try { pending += probe(); }
                catch { /* probe may be stale post-disconnect */ }
            }
        }
        return (bytes, dispatch, pipes, pending);
    }

    private int GetPendingInvocations()
    {
        var (_, _, _, pending) = Sample();
        return pending;
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
            var (bytes, dispatch, pipes, pending) = Sample();
            if (bytes == 0 && dispatch == 0 && pipes == 0 && pending == 0)
            {
                // Drain synchronously-scheduled continuations.
                await Task.Yield();
                (bytes, dispatch, pipes, pending) = Sample();
                if (bytes == 0 && dispatch == 0 && pipes == 0 && pending == 0)
                    return;
            }

            Task signal;
            lock (_gate)
            {
                signal = _changeSignal.Task;
            }

            if (cancellationToken.CanBeCanceled)
                signal = signal.WaitAsync(cancellationToken);

            await signal.ConfigureAwait(false);
        }
    }
}
