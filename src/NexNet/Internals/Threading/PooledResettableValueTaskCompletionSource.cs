namespace NexNet.Internals.Threading;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

/// <summary>
/// A reusable, poolable async operation that implements IValueTaskSource<T>
/// </summary>
internal sealed class PooledResettableValueTaskCompletionSource<T> : IValueTaskSource<T>
{
    private static readonly ConcurrentBag<PooledResettableValueTaskCompletionSource<T>> _pool = new();
    
    private ManualResetValueTaskSourceCore<T> _core;
    private bool _isRented;
    private bool _isCompleted;
    
    /// <summary>
    /// Get the current pool size (for diagnostics)
    /// </summary>
    public static int PoolCount => _pool.Count;

    private PooledResettableValueTaskCompletionSource()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>
    /// Rent an operation from the pool (or create a new one if pool is empty)
    /// </summary>
    public static PooledResettableValueTaskCompletionSource<T> Rent()
    {
        if (!_pool.TryTake(out var operation))
        {
            operation = new PooledResettableValueTaskCompletionSource<T>();
        }
        
        operation._isRented = true;
        return operation;
    }

    /// <summary>
    /// Return the operation to the pool for reuse
    /// </summary>
    public void Return()
    {
        if (!_isRented)
            throw new InvalidOperationException("Cannot return an operation that wasn't rented");

        _isRented = false;
        _isCompleted = false;
        _core.Reset();
        _pool.Add(this);
    }

    /// <summary>
    /// Get the ValueTask for this operation
    /// </summary>
    public ValueTask<T> Task => new ValueTask<T>(this, _core.Version);

    /// <summary>
    /// Signal successful completion
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
    /// Signal completion with an exception
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
    
    public T GetResult(short token)
    {
        return _core.GetResult(token);
    }
    
    public ValueTaskSourceStatus GetStatus(short token)
    {
        return _core.GetStatus(token);
    }

    public void OnCompleted(Action<object?> continuation, object? state,
                           short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
    }
}
