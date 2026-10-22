using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Pools;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Invocation;

internal class SessionInvocationStateManager : ISessionInvocationStateManager
{
    private readonly PoolManager _poolManager;
    private readonly INexusLogger? _logger;
    private readonly INexusSession _nexusSession;
    private ushort _invocationId = 0;
    private readonly HashSet<ushort> _currentInvocations = new HashSet<ushort>();
    private readonly Lock _currentInvocationsLock = new Lock();

    private static int _counter = 0;
    
    private int _id = Interlocked.Increment(ref _counter);

    private readonly ConcurrentDictionary<int, RegisteredInvocationState> _invocationStates;
    //private readonly ConcurrentDictionary<int, RegisteredNexusPipe> _waitingPipes;

    public SessionInvocationStateManager(PoolManager poolManager, INexusLogger? logger, INexusSession nexusSession)
    {
        _invocationStates = new ConcurrentDictionary<int, RegisteredInvocationState>();
        //_waitingPipes = new ConcurrentDictionary<int, RegisteredNexusPipe>();
        _poolManager = poolManager;
        _logger = logger;
        _nexusSession = nexusSession;
    }


    /// <summary>
    /// Generates a unique invocation ID for the current session.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and ensures that the returned ID is not currently in use by any other invocation in the same session.
    /// </remarks>
    /// <param name="addToCurrentInvocations">If true, the invocation ID will be added to the current invocations list.  If false, it will not.</param>
    /// <returns>
    /// A unique ushort value representing the invocation ID.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetNextId(bool addToCurrentInvocations)
    {
        lock (_currentInvocationsLock)
        {
            // If we are not adding to the current invocations, then we can just return the next ID.
            _invocationId++;
            if(!addToCurrentInvocations)
                return _invocationId;

            // Handle wraparound - find next available ID with O(1) HashSet lookup
            int attempts = 0;
            while (_currentInvocations.Contains(_invocationId))
            {
                _invocationId++;
                if (++attempts > ushort.MaxValue)
                {
                    throw new InvalidOperationException("All invocation IDs exhausted");
                }
            }

            _currentInvocations.Add(_invocationId);

            return _invocationId;
        }
    }

    public void UpdateInvocationResult(InvocationResultMessage message)
    {
        // If we can not remove the state any longer, then it has already been handled (or the result is unsolicited).
        // Nothing else holds the message, so return it and its pooled result here.
        if (!_invocationStates.TryRemove(message.InvocationId, out var state))
        {
            // The reply to a call this side canceled after sending it: its id was kept until now.
            ReleaseId(message.InvocationId);
            message.Dispose();
            return;
        }

        ReleaseId(message.InvocationId);

        state.Result = message;

        switch (message.State)
        {
            case InvocationResultMessage.StateType.CompletedResult:
                state.TrySetResult();
                break;
            case InvocationResultMessage.StateType.Exception:
                state.TrySetException(new InvocationException(message.InvocationId));
                break;
            case InvocationResultMessage.StateType.Canceled:
                state.TrySetCanceled();
                break;

            default:
                state.TrySetException(new Exception("Invalid state returned."));
                break;

        }
    }

    // Pooled: awaited or read exactly once, by the proxy.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> InvokeMethodWithResultCore(
        RegisteredInvocationState state,
        ushort methodId,
        Memory<byte> serializedArguments,
        INexusSession session,
        CancellationToken? cancellationToken = null,
        NexNet.Serialization.PooledArrayBufferWriter? argumentsOwner = null)
    {
        if (cancellationToken?.IsCancellationRequested == true)
        {
            argumentsOwner?.Return();
            state.Return();
            return false;
        }

        using var message = _poolManager.Rent<InvocationMessage>();
        message.ArgumentsOwner = argumentsOwner;

        message.MethodId = methodId;
        message.Flags = InvocationFlags.None;

        // Larger than one frame is streamed; larger than the peer accepts fails here, before taking an ID.
        if (serializedArguments.Length > session.SendMaxMessageSize)
        {
            state.Return();
            throw new NexusMessageTooLargeException(serializedArguments.Length, session.SendMaxMessageSize);
        }

        message.SetArguments(serializedArguments);

        // Taken after the size check so a rejected call does not hold an ID.
        message.InvocationId = GetNextId(true);

        state.InvocationId = message.InvocationId;
        state.Owner = this;

        // Registered before the token so that the cancellation callback always finds it, even when the token is
        // already canceled and the callback runs inside Register.
        _invocationStates.TryAdd(message.InvocationId, state);

        if (cancellationToken != null)
        {
            static void Callback(object? arg)
            {
                var state = (RegisteredInvocationState)arg!;
                state.Owner?.CancelInvocation(state);
            }

            state.CancellationRegistration = cancellationToken.Value.Register(Callback, state);
        }

        try
        {
            if (!await session.SendInvocation(message, cancellationToken ?? default).ConfigureAwait(false))
            {
                // The session ended while waiting for invocation credit.
                ForgetUnsent(state);
                state.TrySetCanceled();
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            // Canceled while waiting for invocation credit (or while streaming a large argument): never executed,
            // so no reply comes. Like any call canceled before it is sent, it returns nothing. Nothing awaits the
            // state: return it whether or not the cancellation callback completed it.
            ForgetUnsent(state);
            state.Return();
            return false;
        }
        catch
        {
            ForgetUnsent(state);
            state.Return();
            throw;
        }

        // Canceled while sending: the peer has the invocation now, so tell it. Its id stays reserved for the reply.
        if (!state.MarkSent())
            _nexusSession.ControlSender.RequestInvocationCancellation(message.InvocationId);

        return true;
    }

    /// <summary>
    /// Cancellation callback of a caller's token. Completes the invocation as canceled unless a result, failure or
    /// disconnect completed it first, and asks the peer to cancel it if it was already sent.
    /// </summary>
    /// <remarks>
    /// The id stays reserved: the peer replies to every invocation it received, and a reply for an id that is not
    /// outstanding is a protocol error. The reply finds no state, so it releases the id and is discarded.
    /// </remarks>
    internal void CancelInvocation(RegisteredInvocationState state)
    {
        // Read before the state can be completed and returned to its pool.
        var invocationId = state.InvocationId;
        if (!_invocationStates.TryRemove(new KeyValuePair<int, RegisteredInvocationState>(invocationId, state)))
            return;

        var sent = state.MarkCanceled();
        state.TrySetCanceled();

        if (sent)
            _nexusSession.ControlSender.RequestInvocationCancellation(invocationId);
    }

    /// <summary>
    /// Removes an invocation that was never sent and releases its id, whether or not the cancellation callback
    /// removed it first. No reply will come for it.
    /// </summary>
    private void ForgetUnsent(RegisteredInvocationState state)
    {
        _invocationStates.TryRemove(new KeyValuePair<int, RegisteredInvocationState>(state.InvocationId, state));
        ReleaseId(state.InvocationId);
    }

    public int OutstandingCount => _invocationStates.Count;

    public bool IsOutstanding(ushort invocationId)
    {
        lock (_currentInvocationsLock)
        {
            return _currentInvocations.Contains(invocationId);
        }
    }

    public void FailInvocation(ushort invocationId, Exception exception)
    {
        // The peer refused the invocation, so no reply comes: release the id even if a cancellation already
        // removed the state.
        ReleaseId(invocationId);
        if (!_invocationStates.TryRemove(invocationId, out var state))
            return;

        state.TrySetException(exception);
    }

    public void ReleaseId(ushort invocationId)
    {
        lock (_currentInvocationsLock)
        {
            _currentInvocations.Remove(invocationId);
        }
    }

    public void CancelAll()
    {
        // Each state is completed only by the path that removes it, so a result racing the disconnect can not
        // complete a state this has already completed.
        foreach (var invocationState in _invocationStates)
        {
            if (_invocationStates.TryRemove(invocationState))
                invocationState.Value.TrySetCanceled();
        }

        lock (_currentInvocationsLock)
        {
            _currentInvocations.Clear();
        }

        //foreach (var invocationState in _waitingPipes)
        //    invocationState.Value.Pipe.UpstreamComplete(PipeCompleteMessage.Flags.Canceled);
    }
}
