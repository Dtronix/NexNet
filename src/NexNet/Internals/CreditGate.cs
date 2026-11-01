using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NexNet.Internals;

/// <summary>
/// Outcome of <see cref="CreditGate.WaitAsync"/>.
/// </summary>
internal enum CreditWaitResult : byte
{
    /// <summary>A credit was taken.</summary>
    Acquired,

    /// <summary>No credit arrived within the timeout.</summary>
    TimedOut,

    /// <summary>The gate was closed (the session ended) before a credit arrived.</summary>
    Closed,
}

/// <summary>
/// Counting gate for outbound invocation credits (docs/internals/flow-control.md, section 4.7). Waiters are served
/// in FIFO order. A wait that cannot take a credit at once parks a pooled waiter; it allocates a cancellation
/// registration only for a token that can be canceled and a timer only for a finite timeout, and never a linked
/// token source: <see cref="Close"/> ends every wait when the session ends.
/// </summary>
/// <remarks>
/// Replaces a <see cref="SemaphoreSlim"/>, whose <c>WaitAsync(timeout, token)</c> allocated about 560 bytes per
/// waiting call (task, promise, nodes, registrations) plus the session's linked token source.
/// </remarks>
internal sealed class CreditGate
{
    private readonly object _lock = new();
    private readonly Stack<Waiter> _pool = new();
    private int _count;
    private bool _closed;

    // FIFO of parked waiters, linked through Waiter.Next. Guarded by _lock.
    private Waiter? _head;
    private Waiter? _tail;

    /// <summary>
    /// Credits available now.
    /// </summary>
    public int CurrentCount => Volatile.Read(ref _count);

    /// <summary>
    /// Takes a credit if one is available, without waiting. Credits are handed to parked waiters first, so an
    /// available credit is never one a waiter is owed.
    /// </summary>
    public bool TryTake()
    {
        var count = Volatile.Read(ref _count);
        while (count > 0)
        {
            var observed = Interlocked.CompareExchange(ref _count, count - 1, count);
            if (observed == count)
                return true;
            count = observed;
        }

        return false;
    }

    /// <summary>
    /// Takes a credit, waiting in FIFO order until one is released, <paramref name="timeout"/> passes or the gate is
    /// closed. Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is canceled
    /// first. The returned task must be awaited exactly once.
    /// </summary>
    public ValueTask<CreditWaitResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (TryTake())
            return new ValueTask<CreditWaitResult>(CreditWaitResult.Acquired);

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CreditWaitResult>(cancellationToken);

        Waiter waiter;
        lock (_lock)
        {
            // Rechecked under the lock: Release hands credits to waiters only under it.
            if (TryTake())
                return new ValueTask<CreditWaitResult>(CreditWaitResult.Acquired);

            if (_closed)
                return new ValueTask<CreditWaitResult>(CreditWaitResult.Closed);

            if (timeout == TimeSpan.Zero)
                return new ValueTask<CreditWaitResult>(CreditWaitResult.TimedOut);

            waiter = _pool.Count > 0 ? _pool.Pop() : new Waiter(this);

            // Armed before it is queued, so no release can complete it (and hand it back to the pool) first. A
            // token canceled meanwhile runs its callback inline on this thread; the lock is reentrant and the
            // callback finds the waiter not queued.
            waiter.Arm(timeout, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                waiter.Disarm();
                return ValueTask.FromCanceled<CreditWaitResult>(cancellationToken);
            }

            waiter.Queued = true;
            if (_tail == null)
            {
                _head = _tail = waiter;
            }
            else
            {
                _tail.Next = waiter;
                waiter.Previous = _tail;
                _tail = waiter;
            }

            // Read under the lock: once the lock is released the waiter can complete and be reused.
            return new ValueTask<CreditWaitResult>(waiter, waiter.Version);
        }
    }

    /// <summary>
    /// Releases <paramref name="credits"/>: parked waiters are served first, in FIFO order; the rest become
    /// available.
    /// </summary>
    public void Release(int credits = 1)
    {
        if (credits <= 0)
            return;

        Waiter? served = null;
        lock (_lock)
        {
            while (credits > 0 && _head != null)
            {
                var waiter = Dequeue(_head);
                waiter.Next = served; // reuse the link for the list completed below, outside the lock
                served = waiter;
                credits--;
            }

            if (credits > 0)
                Interlocked.Add(ref _count, credits);
        }

        CompleteAll(served, CreditWaitResult.Acquired);
    }

    /// <summary>
    /// Ends every parked wait with <see cref="CreditWaitResult.Closed"/>; later waits that would park return it at
    /// once. Credits still available can be taken.
    /// </summary>
    public void Close()
    {
        Waiter? closed = null;
        lock (_lock)
        {
            _closed = true;
            while (_head != null)
            {
                var waiter = Dequeue(_head);
                waiter.Next = closed;
                closed = waiter;
            }
        }

        CompleteAll(closed, CreditWaitResult.Closed);
    }

    // The served list is in reverse order; completion order does not matter because continuations run
    // asynchronously, but FIFO order is kept for fairness of who resumes first.
    private static void CompleteAll(Waiter? list, CreditWaitResult result)
    {
        Waiter? reversed = null;
        while (list != null)
        {
            var next = list.Next;
            list.Next = reversed;
            reversed = list;
            list = next;
        }

        while (reversed != null)
        {
            var next = reversed.Next;
            reversed.Next = null;
            reversed.Complete(result);
            reversed = next;
        }
    }

    // Must hold _lock.
    private Waiter Dequeue(Waiter waiter)
    {
        if (waiter.Previous == null) _head = waiter.Next; else waiter.Previous.Next = waiter.Next;
        if (waiter.Next == null) _tail = waiter.Previous; else waiter.Next.Previous = waiter.Previous;
        waiter.Previous = null;
        waiter.Next = null;
        waiter.Queued = false;
        return waiter;
    }

    /// <summary>
    /// Removes <paramref name="waiter"/> if it is still parked; called by its timer or cancellation callback.
    /// Returns false if a release or close already took it.
    /// </summary>
    private bool TryRemove(Waiter waiter)
    {
        lock (_lock)
        {
            if (!waiter.Queued)
                return false;
            Dequeue(waiter);
            return true;
        }
    }

    private void Return(Waiter waiter)
    {
        lock (_lock)
            _pool.Push(waiter);
    }

    private sealed class Waiter : IValueTaskSource<CreditWaitResult>
    {
        private static readonly Action<object?> s_canceled = static state => ((Waiter)state!).OnCanceled();
        private static readonly TimerCallback s_timedOut = static state => ((Waiter)state!).OnTimedOut();

        private readonly CreditGate _gate;
        private ManualResetValueTaskSourceCore<CreditWaitResult> _core;
        private CancellationTokenRegistration _registration;
        private CancellationToken _token;
        private ITimer? _timer;

        // A waiter whose timer or cancellation callback may still run is not reused: the callback could otherwise
        // act on the next wait.
        private bool _reusable;

        // Guarded by the gate's lock.
        public bool Queued;
        public Waiter? Next;
        public Waiter? Previous;

        public Waiter(CreditGate gate)
        {
            _gate = gate;
            // Completed under no lock, but by the read loop: continuations must not run inline there.
            _core.RunContinuationsAsynchronously = true;
        }

        public short Version => _core.Version;

        public void Arm(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _reusable = true;
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                // Real time, as with the SemaphoreSlim it replaces: the timeout bounds waiting on the network peer.
                _timer = TimeProvider.System.CreateTimer(s_timedOut, this, timeout, Timeout.InfiniteTimeSpan);
                _reusable = false;
            }

            if (cancellationToken.CanBeCanceled)
            {
                _token = cancellationToken;
                _registration = cancellationToken.UnsafeRegister(s_canceled, this);
            }
        }

        public void Complete(CreditWaitResult result)
        {
            Disarm();
            _core.SetResult(result);
        }

        private void OnTimedOut()
        {
            if (!_gate.TryRemove(this))
                return;
            Disarm();
            _core.SetResult(CreditWaitResult.TimedOut);
        }

        private void OnCanceled()
        {
            if (!_gate.TryRemove(this))
                return;
            var token = _token;
            Disarm();
            _core.SetException(new OperationCanceledException(token));
        }

        /// <summary>
        /// Stops the timer and the cancellation registration. Called once per wait, by whichever path removed the
        /// waiter from the queue (or by <see cref="CreditGate.WaitAsync"/> for a token canceled while arming).
        /// </summary>
        public void Disarm()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            if (_token.CanBeCanceled)
            {
                // Unregister does not wait for a running callback (Dispose would, and could deadlock with it). If the
                // callback is running or ran, it may still touch this waiter, so it is not reused.
                if (!_registration.Unregister())
                    _reusable = false;
                _registration = default;
                _token = default;
            }
        }

        public CreditWaitResult GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                if (_reusable)
                {
                    _core.Reset();
                    _gate.Return(this);
                }
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
