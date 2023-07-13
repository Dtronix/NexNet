using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NexNet.Internals.Tasks;

namespace NexNet.Internals;

internal sealed class ManualResetAwaiter : ITaskCompletionAwaiter
{
    private Action? _continuation;
    private Exception? _exception;
    private bool _isCompleted;

    public void OnCompleted(Action continuation)
    {
        //Console.WriteLine("Registered OnCompleted");
        if (_continuation != null)
            throw new InvalidOperationException("This ReusableTaskCompletionSource instance has already been listened");
        // It is possible for the continuation to be registered post transition to completion.
        // Fire now if that is the case.

        //Console.WriteLine($"OnCompleted IsCompleted:{_isCompleted}");
        if (_isCompleted)
        {
            
            continuation.Invoke();
        }
        else
        {
            _continuation = continuation;
        }
    }

    public bool IsCompleted
    {
        get
        {
            //Console.WriteLine($"Retrieved IsCompleted");
            return _isCompleted;
        }
        private set => _isCompleted = value;
    }

    public bool IsCanceled { get; private set; }

    public void GetResult()
    {
        //Console.WriteLine("ManualResetAwaiter.GetResult()");
        if (_exception != null)
            throw _exception;
    }

    /// <summary>
    /// Attempts to transition the completion state.
    /// </summary>
    /// <param name="result">Result to have the awaited task return.</param>
    /// <returns>True on success, false otherwise.</returns>
    public bool TrySetResult()
    {
        //Console.WriteLine($"ManualResetAwaiter.IsCompleted: {_isCompleted}");

        if (_isCompleted)
            return false;

        _isCompleted = true;

        if (_continuation == null)
            //Console.WriteLine("_continuation null");

        _continuation?.Invoke();

        return true;
    }

    /// <summary>
    /// Attempts to transition the exception state.
    /// </summary>
    /// <returns></returns>
    public bool TrySetException(Exception exception)
    {
        if (_isCompleted)
            return false;

        _isCompleted = true;
        _exception = exception;

        _continuation?.Invoke();
        return true;
    }

    /// <summary>
    /// Attempts to transition to the canceled state.
    /// </summary>
    /// <returns></returns>
    public bool TrySetCanceled()
    {
        if (_isCompleted)
            return false;

        _isCompleted = true;
        IsCanceled = true;
        _exception = new OperationCanceledException();

        _continuation?.Invoke();
        return true;
    }

    /// <summary>
    /// Reset the awaiter to initial status
    /// </summary>
    /// <returns></returns>
    public void Reset()
    {
        //Console.WriteLine("ManualResetAwaiter Reset");
        _continuation = null;
        _exception = null;
        _isCompleted = false;
        IsCanceled = false;
    }
    public ITaskCompletionAwaiter GetAwaiter()
    {
        return this;
    }
}

/// <summary>
/// Manually resettable awaiter for reuse in high performance situations.
/// </summary>
internal sealed class ManualResetAwaiterSource : IManualResetAwaiterSource
{
    private readonly ManualResetAwaiter _awaiter = new();

    public ITaskCompletionAwaiter Awaiter => _awaiter;
    /// <summary>
    /// Attempts to transition the completion state.
    /// </summary>
    /// <param name="result">Result to have the awaited task return.</param>
    /// <returns>True on success, false otherwise.</returns>
    public bool TrySetResult()
    {
        return _awaiter.TrySetResult();
    }

    /// <inheritdoc />
    public bool TrySetException(Exception exception)
    {
        return _awaiter.TrySetException(exception);
    }

    /// <inheritdoc />
    public bool TrySetCanceled()
    {
        return _awaiter.TrySetCanceled();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _awaiter.Reset();
    }

    /// <summary>
    /// Returns a task for the current awaiter state.
    /// </summary>
    /// <returns>Task for the state.</returns>
    public Task ToTask()
    {
        return Task.Run(async () => await _awaiter);
    }
}
