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

    // 1 once a result, cancellation or exception has been set. Claimed atomically: the cancellation callback can
    // race with the sending task.
    private int _complete;

    public bool IsComplete => Volatile.Read(ref _complete) != 0;

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
        Volatile.Write(ref _complete, 0);
        IsCanceled = false;
        Exception = null;
    }

    public short Version => _source.Version;
    public bool TrySetResult()
    {
        if (Interlocked.Exchange(ref _complete, 1) != 0)
            return false;

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
        if (Interlocked.Exchange(ref _complete, 1) != 0)
            return false;
        IsCanceled = true;
        NotifyConnection = notifyConnection;
        _source.SetResult(false);
        return true;
    }



    public bool TrySetException(Exception exception)
    {
        if (Interlocked.Exchange(ref _complete, 1) != 0)
            return false;
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
