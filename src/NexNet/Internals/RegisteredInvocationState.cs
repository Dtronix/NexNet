﻿using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pools;

namespace NexNet.Internals;

/// <summary>
/// A pending invocation that expects a result. It backs the <see cref="ValueTask"/> the proxy returns, so a call
/// allocates nothing while it waits: reading the result maps the outcome and returns the state to its pool.
/// </summary>
/// <remarks>
/// The returned <see cref="ValueTask"/> may be consumed once: awaited, blocked on, or converted with
/// <see cref="ValueTask.AsTask"/>. A second consumption fails with <see cref="InvalidOperationException"/> instead of
/// observing another call. Blocking on a call that has not completed waits for it.
/// </remarks>
internal class RegisteredInvocationState : IValueTaskSource, IResettable
{
    private static readonly ResettablePool<RegisteredInvocationState> _pool = new(128);

    /// <summary>
    /// Rents a state for a call without a return value.
    /// </summary>
    public static RegisteredInvocationState Rent() => _pool.Rent();

    /// <summary>
    /// Returns the state to its pool.
    /// </summary>
    public virtual void Return() => _pool.Return(this);

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

    // Created the first time a caller blocks on an unfinished call; kept with the pooled state.
    private ManualResetEventSlim? _blocking;

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
    /// Registration on the caller's cancellation token. Unregistered when the invocation completes, so a call whose
    /// result is never read does not keep it; disposed when the state returns to its pool.
    /// </summary>
    public CancellationTokenRegistration CancellationRegistration { get; set; }

    public RegisteredInvocationState()
    {
        // Continuations never run on the session's read loop.
        _source.RunContinuationsAsynchronously = true;
    }


    public void Reset()
    {
        // Dispose rather than only Unregister: waits for a callback already running, which could otherwise cancel
        // the invocation that reuses this state. The callback never runs the awaiting continuation inline, so this
        // can not wait on itself.
        CancellationRegistration.Dispose();
        CancellationRegistration = default;
        _source.Reset();
        _blocking?.Reset();
        Volatile.Write(ref _complete, 0);
        Volatile.Write(ref _sendPhase, (int)SendPhase.Sending);
        IsCanceled = false;
        Exception = null;
        Owner = null;
        Result?.Dispose();
        Result = null;
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
        if (!TryClaim())
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
        if (!TryClaim())
            return false;

        IsCanceled = true;
        _source.SetResult(false);
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        if (!TryClaim())
            return false;

        Exception = exception;
        _source.SetResult(false);
        return true;
    }

    private bool TryClaim()
    {
        if (Interlocked.Exchange(ref _complete, 1) != 0)
            return false;

        // Does not wait: this may run inside the callback itself.
        CancellationRegistration.Unregister();
        return true;
    }

    /// <summary>
    /// Waits until the call completes when a caller blocks on it (<c>.Result</c>,
    /// <c>.GetAwaiter().GetResult()</c>) before it has. Throws if <paramref name="token"/> is not the current call's.
    /// </summary>
    protected void WaitIfPending(short token)
    {
        if (_source.GetStatus(token) != ValueTaskSourceStatus.Pending)
            return;

        var blocking = _blocking ??= new ManualResetEventSlim();
        _source.OnCompleted(static state => ((ManualResetEventSlim)state!).Set(), blocking, token,
            ValueTaskSourceOnCompletedFlags.None);
        blocking.Wait();
    }

    /// <summary>
    /// Consumes the completed call: throws if <paramref name="token"/> is not the current call's. Called before
    /// anything that returns the state, so a second consumption never returns a state another call is using.
    /// </summary>
    protected void Consume(short token) => _source.GetResult(token);

    /// <summary>
    /// Throws the call's failure, if any: a local failure, a cancellation, or the peer's refusal.
    /// </summary>
    protected void ThrowIfFailed()
    {
        // Failed before any result arrived (e.g. the peer refused a large argument).
        if (Result == null && Exception is { } failure)
            ExceptionDispatchInfo.Throw(failure);

        if (IsCanceled)
            throw new TaskCanceledException();

        switch (Result!.State)
        {
            case InvocationResultMessage.StateType.CompletedResult:
                return;

            case InvocationResultMessage.StateType.Exception:
                throw new ProxyRemoteInvocationException();

            case InvocationResultMessage.StateType.Unauthorized:
                throw new ProxyUnauthorizedException();

            default:
                throw new InvalidOperationException($"Unknown state {Result.State}");
        }
    }

    public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

    // The awaiter's flags are kept: an await without ConfigureAwait(false) resumes on its context.
    public void OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _source.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.GetResult(short token)
    {
        WaitIfPending(token);
        Consume(token);
        try
        {
            ThrowIfFailed();
        }
        finally
        {
            Return();
        }
    }
}

/// <summary>
/// A pending invocation whose method returns <typeparamref name="T"/>. Pooled per result type.
/// </summary>
internal sealed class RegisteredInvocationState<T> : RegisteredInvocationState, IValueTaskSource<T>
{
    private static readonly ResettablePool<RegisteredInvocationState<T>> _typedPool = new(128);

    /// <summary>
    /// Rents a state for a call returning <typeparamref name="T"/>.
    /// </summary>
    public new static RegisteredInvocationState<T> Rent() => _typedPool.Rent();

    /// <inheritdoc />
    public override void Return() => _typedPool.Return(this);

    T IValueTaskSource<T>.GetResult(short token)
    {
        WaitIfPending(token);
        Consume(token);
        try
        {
            ThrowIfFailed();

            if (!Result!.TryGetResult<T>(out var result))
                throw new InvalidOperationException($"Could not get result of type {typeof(T)} from invocation result.");

            return result!;
        }
        finally
        {
            Return();
        }
    }
}
