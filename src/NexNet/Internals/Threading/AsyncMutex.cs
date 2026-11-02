using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NexNet.Internals.Threading;

/// <summary>
/// Asynchronous mutual-exclusion lock for the session's write path. Waiters are served in FIFO order; the lock is
/// handed directly to the next waiter on release. A waiting caller parks a pooled waiter and allocates a cancellation
/// registration only for a token that can be canceled.
/// </summary>
/// <remarks>
/// Replaces the vendored MutexSlim, which allocated a task and a pending-token object per contended wait and a
/// timer per contention episode for a timeout NexNet does not use.
/// </remarks>
internal sealed class AsyncMutex
{
    // Spins before parking when the lock is held but nobody waits: the write path holds it briefly.
    private const int SpinCount = 10;

    private readonly object _lock = new();
    private readonly Stack<Waiter> _pool = new();

    // 0 free, 1 held. Taken by CAS; handed to a waiter under _lock without passing through 0.
    private int _state;

    // Generation of the current holder; a LockToken releases only the generation it was issued.
    private int _holder;
    private int _generation;

    // FIFO of parked waiters, linked through Waiter.Next. Guarded by _lock.
    private Waiter? _head;
    private Waiter? _tail;

    /// <summary>
    /// True if the lock is free.
    /// </summary>
    public bool IsAvailable => Volatile.Read(ref _state) == 0;

    /// <summary>
    /// Takes the lock if it is free and nobody waits; otherwise returns a token whose
    /// <see cref="LockToken.Success"/> is false. Never waits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LockToken TryEnter()
        => Interlocked.CompareExchange(ref _state, 1, 0) == 0 ? Acquired() : default;

    /// <summary>
    /// Takes the lock, waiting in FIFO order. Throws <see cref="TaskCanceledException"/> if
    /// <paramref name="cancellationToken"/> is canceled first. The returned task must be awaited exactly once.
    /// </summary>
    public ValueTask<LockToken> EnterAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromException<LockToken>(new TaskCanceledException(null, null, cancellationToken));

        if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            return new ValueTask<LockToken>(Acquired());

        // Held, nobody waiting: the holder usually finishes within a few spins.
        if (Volatile.Read(ref _head) == null)
        {
            var spinner = new SpinWait();
            for (var i = 0; i < SpinCount; i++)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
                if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                    return new ValueTask<LockToken>(Acquired());
            }
        }

        lock (_lock)
        {
            // Rechecked under the lock: Release decides between handing over and freeing only under it.
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                return new ValueTask<LockToken>(Acquired());

            var waiter = _pool.Count > 0 ? _pool.Pop() : new Waiter(this);

            // Armed before it is queued, so no release can complete it (and hand it back to the pool) first. A token
            // canceled meanwhile runs its callback inline on this thread; the lock is reentrant and the callback
            // finds the waiter not queued.
            waiter.Arm(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                waiter.Disarm();
                return ValueTask.FromException<LockToken>(new TaskCanceledException(null, null, cancellationToken));
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
            return new ValueTask<LockToken>(waiter, waiter.Version);
        }
    }

    // Called by the thread that just set _state to 1.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LockToken Acquired()
    {
        var generation = NextGeneration();
        Volatile.Write(ref _holder, generation);
        return new LockToken(this, generation);
    }

    private int NextGeneration()
    {
        // 0 marks a token that holds nothing.
        var generation = Interlocked.Increment(ref _generation);
        return generation == 0 ? Interlocked.Increment(ref _generation) : generation;
    }

    private void Release(int generation)
    {
        Waiter? next;
        LockToken handed = default;
        lock (_lock)
        {
            if (Volatile.Read(ref _state) != 1 || Volatile.Read(ref _holder) != generation)
                throw new InvalidOperationException("Released a write lock token that does not hold the lock.");

            next = _head;
            if (next == null)
            {
                Volatile.Write(ref _holder, 0);
                Volatile.Write(ref _state, 0);
                return;
            }

            // Hand over without freeing, so no TryEnter can barge in ahead of the queue.
            Dequeue(next);
            var nextGeneration = NextGeneration();
            Volatile.Write(ref _holder, nextGeneration);
            handed = new LockToken(this, nextGeneration);
        }

        next.Complete(handed);
    }

    // Must hold _lock.
    private void Dequeue(Waiter waiter)
    {
        if (waiter.Previous == null) _head = waiter.Next; else waiter.Previous.Next = waiter.Next;
        if (waiter.Next == null) _tail = waiter.Previous; else waiter.Next.Previous = waiter.Previous;
        waiter.Previous = null;
        waiter.Next = null;
        waiter.Queued = false;
    }

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

    /// <summary>
    /// Holds the lock until disposed. The default value holds nothing and disposing it does nothing.
    /// </summary>
    public readonly struct LockToken : IDisposable
    {
        private readonly AsyncMutex? _owner;
        private readonly int _generation;

        internal LockToken(AsyncMutex owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        /// <summary>
        /// True if this token holds the lock.
        /// </summary>
        public bool Success => _owner != null;

        /// <summary>
        /// Releases the lock. Throws <see cref="InvalidOperationException"/> if this token no longer holds it
        /// (released twice).
        /// </summary>
        public void Dispose() => _owner?.Release(_generation);
    }

    private sealed class Waiter : IValueTaskSource<LockToken>
    {
        private static readonly Action<object?> s_canceled = static state => ((Waiter)state!).OnCanceled();

        private readonly AsyncMutex _mutex;
        private ManualResetValueTaskSourceCore<LockToken> _core;
        private CancellationTokenRegistration _registration;
        private CancellationToken _token;

        // A waiter whose cancellation callback may still run is not reused: the callback could otherwise act on the
        // next wait.
        private bool _reusable;

        // Guarded by the mutex's lock.
        public bool Queued;
        public Waiter? Next;
        public Waiter? Previous;

        public Waiter(AsyncMutex mutex)
        {
            _mutex = mutex;
            // Completed by the releasing holder: the next holder's continuation must not run inside its Dispose.
            _core.RunContinuationsAsynchronously = true;
        }

        public short Version => _core.Version;

        public void Arm(CancellationToken cancellationToken)
        {
            _reusable = true;
            if (cancellationToken.CanBeCanceled)
            {
                _token = cancellationToken;
                _registration = cancellationToken.UnsafeRegister(s_canceled, this);
            }
        }

        public void Complete(LockToken token)
        {
            Disarm();
            _core.SetResult(token);
        }

        private void OnCanceled()
        {
            if (!_mutex.TryRemove(this))
                return;
            var token = _token;
            Disarm();
            _core.SetException(new TaskCanceledException(null, null, token));
        }

        /// <summary>
        /// Stops the cancellation registration. Called once per wait, by whichever path removed the waiter from the
        /// queue (or by <see cref="AsyncMutex.EnterAsync"/> for a token canceled while arming).
        /// </summary>
        public void Disarm()
        {
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

        public LockToken GetResult(short token)
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
                    _mutex.Return(this);
                }
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
