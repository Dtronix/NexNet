using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace NexNet.Pools;

/// <summary>
/// A reusable, poolable async operation that implements IValueTaskSource&lt;T&gt;.
/// </summary>
/// <typeparam name="T">Result type.</typeparam>
internal sealed class PooledValueTaskSource<T> : IValueTaskSource<T>
{
    private static readonly ConcurrentBag<PooledValueTaskSource<T>> _pool = new();
    private static int _poolCount;

    /// <summary>
    /// Maximum number of operations to keep in the pool.
    /// </summary>
    internal const int MaxPoolSize = 128;

    private ManualResetValueTaskSourceCore<T> _core;
    private bool _isRented;
    private bool _isCompleted;

    /// <summary>
    /// Gets the current pool count (for diagnostics).
    /// </summary>
    public static int PoolCount => Volatile.Read(ref _poolCount);

    private PooledValueTaskSource()
    {
        _core.RunContinuationsAsynchronously = true;
        _isRented = true;
    }

    /// <summary>
    /// Rents an operation from the pool (or creates a new one).
    /// </summary>
    public static PooledValueTaskSource<T> Rent()
    {
        if (_pool.TryTake(out var operation))
        {
            Interlocked.Decrement(ref _poolCount);
        }
        else
        {
            operation = new PooledValueTaskSource<T>();
        }

        operation._isRented = true;
        return operation;
    }

    /// <summary>
    /// Returns the operation to the pool for reuse.
    /// </summary>
    public void Return()
    {
        if (!_isRented)
            throw new InvalidOperationException("Cannot return an operation that wasn't rented");

        _isRented = false;
        Reset();

        // Only pool if under limit
        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            _pool.Add(this);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }

    /// <summary>
    /// Resets the operation for reuse.
    /// </summary>
    public void Reset()
    {
        _isCompleted = false;
        _core.Reset();
    }

    /// <summary>
    /// Gets the ValueTask for this operation.
    /// </summary>
    public ValueTask<T> Task => new(this, _core.Version);

    /// <summary>
    /// Signals successful completion.
    /// </summary>
    public bool TrySetResult(T result)
    {
        if (_isCompleted)
            return false;

        if (!_isRented)
            throw new InvalidOperationException("Operation must be rented before use");

        _isCompleted = true;
        _core.SetResult(result);

        return true;
    }

    /// <summary>
    /// Signals completion with an exception.
    /// </summary>
    public bool TrySetException(Exception error)
    {
        if (_isCompleted)
            return false;

        if (!_isRented)
            throw new InvalidOperationException("Operation must be rented before use");

        _isCompleted = true;
        _core.SetException(error);

        return true;
    }

    /// <inheritdoc />
    public T GetResult(short token)
    {
        return _core.GetResult(token);
    }

    /// <inheritdoc />
    public ValueTaskSourceStatus GetStatus(short token)
    {
        return _core.GetStatus(token);
    }

    /// <inheritdoc />
    public void OnCompleted(Action<object?> continuation, object? state,
                           short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
    }
}
