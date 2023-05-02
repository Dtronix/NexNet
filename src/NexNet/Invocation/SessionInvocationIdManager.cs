using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Invocation;

internal class SessionInvocationStateManager
{
    private readonly CacheManager _cacheManager;
    private int _invocationId = 0;

    private readonly ConcurrentDictionary<int, RegisteredInvocationState> _invocationStates;

    public SessionInvocationStateManager(CacheManager cacheManager)
    {
        _invocationStates = new ConcurrentDictionary<int, RegisteredInvocationState>();
        _cacheManager = cacheManager;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNextId()
    {
        return Interlocked.Increment(ref _invocationId);
    }


    public void UpdateInvocationResult(InvocationProxyResultMessage message)
    {
        // If we can not remove the state any longer, then it has already been handled.
        if (!_invocationStates.TryRemove(message.InvocationId, out var state))
            return;
        state.Result = message;

        switch (message.State)
        {
            //case InvocationProxyResultMessage.StateType.RequestCancel:
            //    state.TrySetCanceled();
            //    break;
            case InvocationProxyResultMessage.StateType.CompletedResult:
                state.TrySetResult();
                break;
            case InvocationProxyResultMessage.StateType.Exception:
                state.TrySetException(new InvocationException(message.InvocationId));
                break;

            default:
                state.TrySetException(new Exception("Invalid state returned."));
                break;

        }
    }


    public async ValueTask<RegisteredInvocationState?> InvokeMethodWithResultCore(
        ushort methodId,
        byte[]? arguments,
        INexNetSession session,
        CancellationToken? cancellationToken = null)
    {
        if (cancellationToken?.IsCancellationRequested == true)
            return null;

        var message = _cacheManager.InvocationRequestDeserializer.Rent();

        message.InvocationId = GetNextId();
        message.MethodId = methodId;
        message.Arguments = arguments;
        message.Flags = InvocationRequestMessage.InvocationFlags.None; 

        var state = _cacheManager.RegisteredInvocationStateCache.Rent();

        state.InvocationId = message.InvocationId;
        // Reset state information for this invocation.

        if (cancellationToken != null)
        {
            static void Callback(object? arg)
            {
                //Console.WriteLine("Canceled");
                var state = (RegisteredInvocationState)arg!;
                state.TrySetCanceled();
            }

            cancellationToken.Value.Register(Callback, state);
        }

        state.Created = Environment.TickCount64;

        // Add the state information to the active states.

        if (cancellationToken == null || cancellationToken?.IsCancellationRequested == false)
        {
            _invocationStates.TryAdd(message.InvocationId, state);

            await session.SendHeaderWithBody(message);
        }
        else
        {
            _cacheManager.RegisteredInvocationStateCache.Return(state);
            _cacheManager.InvocationRequestDeserializer.Return(message);
            return null;
        }

        // Return the message to the cache
        _cacheManager.InvocationRequestDeserializer.Return(message);
        return state;
    }

    public void CancelAll()
    {
        foreach (var invocationState in _invocationStates)
            invocationState.Value.TrySetCanceled();
    }
}
