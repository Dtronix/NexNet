using System;
using System.Threading;
using System.Threading.Tasks.Sources;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pools;

namespace NexNet.Internals;

internal class RegisteredInvocationState : IValueTaskSource<bool>, IResettable
{
    /// <summary>
    /// Id used for invocation management.
    /// </summary>
    public ushort InvocationId { get; set; }

    /// <summary>
    /// Manager the invocation is registered with. Its cancellation callback removes the invocation through it.
    /// </summary>
    public SessionInvocationStateManager? Owner { get; set; }

    // Mutable struct.
    private ManualResetValueTaskSourceCore<bool> _source;

    // 1 once a result, cancellation or exception has been set. Claimed atomically: the cancellation callback can
    // race with the sending task.
    private int _complete;

    // SendPhase value. Decides who sends the cancellation frame when the call is canceled during its send.
    private int _sendPhase;

    /// <summary>
    /// Sending: the invocation is being sent. Sent: the peer has the invocation. CanceledWhileSending: canceled
    /// before the send finished; the sender sends the cancellation frame once the invocation is out.
    /// </summary>
    public enum SendPhase
    {
        Sending = 0,
        Sent = 1,
        CanceledWhileSending = 2
    }

    public bool IsComplete => Volatile.Read(ref _complete) != 0;

    public bool IsCanceled { get; private set; }

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
        // Dispose rather than Unregister: waits for a callback already running, which could otherwise cancel the
        // invocation that reuses this state. The callback never runs the awaiting continuation inline, so this
        // can not wait on itself.
        CancellationRegistration.Dispose();
        CancellationRegistration = default;
        _source.Reset();
        Volatile.Write(ref _complete, 0);
        Volatile.Write(ref _sendPhase, (int)SendPhase.Sending);
        IsCanceled = false;
        Exception = null;
        Owner = null;
    }

    /// <summary>
    /// Marks the invocation as sent. Returns false if it was canceled while sending; the caller then sends the
    /// cancellation frame.
    /// </summary>
    public bool MarkSent()
        => Interlocked.CompareExchange(ref _sendPhase, (int)SendPhase.Sent, (int)SendPhase.Sending)
           == (int)SendPhase.Sending;

    /// <summary>
    /// Records a cancellation. Returns true if the invocation was already sent; the caller then sends the
    /// cancellation frame. Otherwise the sender sends it once the send finishes.
    /// </summary>
    public bool MarkCanceled()
        => Interlocked.CompareExchange(ref _sendPhase, (int)SendPhase.CanceledWhileSending, (int)SendPhase.Sending)
           == (int)SendPhase.Sent;

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
    /// <returns>True if the call succeeded.  False if the state has already been set.</returns>
    public bool TrySetCanceled()
    {
        if (Interlocked.Exchange(ref _complete, 1) != 0)
            return false;
        IsCanceled = true;
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
