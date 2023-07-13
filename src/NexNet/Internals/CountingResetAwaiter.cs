using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NexNet.Internals;

internal sealed class CountingResetAwaiter : INotifyCompletion, ITaskCompletionAwaiter
{
    private Action? _continuation;
    private Exception? _exception;
    private int _invocationsFired = 0;
    public void OnCompleted(Action continuation)
    {
        if (_continuation != null)
            throw new InvalidOperationException("This ReusableTaskCompletionSource instance has already been listened");

        var decrementedValue = Interlocked.Decrement(ref _invocationsFired);


        Console.WriteLine($"OnCompleted Decremented {decrementedValue}");
        // It is possible for the continuation to be registered post transition to completion.
        // Fire now if that is the case.
        if (_invocationsFired >= 0)
        {
            continuation.Invoke();
            Reset();
        }
        else
        {
            _continuation = continuation;
        }
    }

    public bool IsCompleted { get; private set; }
    public bool IsCanceled { get; private set; }

    public void GetResult()
    {

        var decrementedValue = Interlocked.Decrement(ref _invocationsFired);
        Console.WriteLine($"GetResult Decremented {decrementedValue}");

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
        var fireCount = Interlocked.Increment(ref _invocationsFired);

        Console.WriteLine($"CountingResetAwaiter.FireCount: {fireCount}");

        IsCompleted = true;
        if (fireCount == 0)
        {
            Interlocked.Decrement(ref _invocationsFired);
            _continuation!.Invoke();
            Reset();
            return true;
        }

        return true;
    }
    /*
    /// <summary>
    /// Attempts to transition the exception state.
    /// </summary>
    /// <returns></returns>
    public bool TrySetException(Exception exception)
    {
        if (IsCompleted)
            return false;

        IsCompleted = true;
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
        if (IsCompleted)
            return false;

        IsCompleted = true;
        IsCanceled = true;
        _exception = new OperationCanceledException();

        _continuation?.Invoke();
        return true;
    }*/

    /// <summary>
    /// Reset the awaiter to initial status
    /// </summary>
    /// <returns></returns>
    private void Reset()
    {
        Console.WriteLine("CountingResetAwaiter Reset");
        _continuation = null;
        _exception = null;
        //IsCompleted = false;
        IsCanceled = false;
    }
    public ITaskCompletionAwaiter GetAwaiter()
    {
        return this;
    }
}
