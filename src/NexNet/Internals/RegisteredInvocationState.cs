using System;
using System.Threading;
using System.Threading.Tasks.Sources;
using NexNet.Messages;
using NexNet.Pools;

namespace NexNet.Internals;

internal class RegisteredInvocationState : IValueTaskSource<bool>, IResettable
{
    /// <summary>
    /// Id used for invocation management.
    /// </summary>
    public ushort InvocationId { get; set; }

    // Mutable struct.
    private ManualResetValueTaskSourceCore<bool> _source;

    public bool IsComplete { get; set; }

    public bool IsCanceled { get; set; }

    public bool NotifyConnection { get; set; }
    public Exception? Exception { get; set; }

    public InvocationResultMessage? Result { get; set; } = null;

    /// <summary>
    /// Registration on the caller's cancellation token. Released when the state returns to its pool.
    /// </summary>
    public CancellationTokenRegistration CancellationRegistration { get; set; }

    public RegisteredInvocationState()
    {
        // Ensure all continuations run asynchronously to prevent deadlocks.
        _source.RunContinuationsAsynchronously = true;
    }


    public void Reset()
    {
        // Unregister rather than Dispose: Dispose blocks while the callback runs on another thread.
        CancellationRegistration.Unregister();
        CancellationRegistration = default;
        _source.Reset();
        IsComplete = false;
        IsCanceled = false;
        Exception = null;
    }

    public short Version => _source.Version;
    public bool TrySetResult()
    {
        if (IsComplete)
            return false;
        IsComplete = true;

        _source.SetResult(true);
        return true;
    }

    /// <summary>
    /// Cancels the current pending invocation.
    /// </summary>
    /// <param name="notifyConnection">Set to true to send a notification that this invocation
    /// has been canceled.  False to just cancel.</param>
    /// <returns>True if the call succeeded.  False if the state has already been set.</returns>
    public bool TrySetCanceled(bool notifyConnection)
    {
        if (IsComplete)
            return false;
        IsComplete = true;
        IsCanceled = true;
        NotifyConnection = notifyConnection;
        _source.SetResult(false);
        return true;
    }



    public bool TrySetException(Exception exception)
    {
        if (IsComplete)
            return false;
        IsComplete = true;
        Exception = exception;

        _source.SetResult(false);
        return true;
    }

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token)
    {
        return _source.GetStatus(token);
    }

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _source.OnCompleted(continuation, state, token, ValueTaskSourceOnCompletedFlags.None);
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _source.GetResult(token);
}
