using System;
using System.Threading.Tasks.Sources;
using NexNet.Cache;
using NexNet.Messages;

namespace NexNet.Internals;

internal class RegisteredInvocationState : IValueTaskSource<bool>, IResettable
{
    //private static readonly TaskCanceledException _tce = new TaskCanceledException();
    public int InvocationId { get; set; }
    //public Sequence<byte> Sequence;
    //public readonly MemoryPackWriterOptionalState OptionalState;
    //public ManualResetAwaiterSource ResetEvent { get; } = new ManualResetAwaiterSource();

    private ManualResetValueTaskSourceCore<bool> _source = new ManualResetValueTaskSourceCore<bool>(); // Mutable struct, not readonly

    public bool IsComplete { get; set; }
    public bool IsCanceled { get; set; }
    public Exception? Exception { get; set; }


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

    public bool TrySetCanceled()
    {
        if (IsComplete)
            return false;
        IsComplete = true;
        IsCanceled = true;

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

    public InvocationProxyResultMessage Result { get; set; }

    /// <summary>
    /// Environment.Ticks when this state was instanced.
    /// </summary>
    public long Created { get; set; }

    public RegisteredInvocationState()
    {
        // Sequence = new Sequence<byte>(ArrayPool<byte>.Shared);
        //OptionalState = MemoryPackWriterOptionalStatePool.Rent(MemoryPackSerializerOptions.Utf8);
    }
}
