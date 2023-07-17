using System;
using System.IO.Pipelines;
using System.Threading.Tasks.Sources;
using NexNet.Cache;
using NexNet.Messages;

namespace NexNet.Internals;

internal class RegisteredInvocationState : IValueTaskSource<bool>, IResettable
{
    /// <summary>
    /// Id used for invocation management.
    /// </summary>
    public int InvocationId { get; set; }

    // Mutable struct.
    private ManualResetValueTaskSourceCore<bool> _source = new ManualResetValueTaskSourceCore<bool>();

    public bool IsComplete { get; set; }

    public bool IsCanceled { get; set; }

    public bool NotifyConnection { get; set; }
    public Exception? Exception { get; set; }

    public InvocationResultMessage Result { get; set; } = null!;

    /// <summary>
    /// Environment.Ticks when this state was instanced.
    /// </summary>
    public long Created { get; set; }



    public void Reset()
    {
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
        _source.OnCompleted(continuation, state, token, flags);
    }

    bool IValueTaskSource<bool>.GetResult(short token) => _source.GetResult(token);
    public RegisteredInvocationState()
    {
        // Sequence = new Sequence<byte>(ArrayPool<byte>.Shared);
        //OptionalState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Utf8);
    }
}
