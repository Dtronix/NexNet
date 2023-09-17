using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Invocation;

internal class SessionInvocationStateManager
{
    private readonly CacheManager _cacheManager;
    private readonly INexusLogger? _logger;
    private ushort _invocationId = 0;
    private readonly List<ushort> _currentInvocations = new List<ushort>();

    private readonly ConcurrentDictionary<int, RegisteredInvocationState> _invocationStates;
    //private readonly ConcurrentDictionary<int, RegisteredNexusPipe> _waitingPipes;

    public SessionInvocationStateManager(CacheManager cacheManager, INexusLogger? logger)
    {
        _invocationStates = new ConcurrentDictionary<int, RegisteredInvocationState>();
        //_waitingPipes = new ConcurrentDictionary<int, RegisteredNexusPipe>();
        _cacheManager = cacheManager;
        _logger = logger;
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
        lock (_currentInvocations)
        {
            // If we are not adding to the current invocations, then we can just return the next ID.
            _invocationId++;
            if(!addToCurrentInvocations)
                return _invocationId;

            while (_currentInvocations.Contains(_invocationId))
                _invocationId++;

            _currentInvocations.Add(_invocationId);

            return _invocationId;
        }
    }

    public void UpdateInvocationResult(InvocationResultMessage message)
    {
        // If we can not remove the state any longer, then it has already been handled.
        if (!_invocationStates.TryRemove(message.InvocationId, out var state))
            return;

        // Remove the invocation from the current invocations list.
        lock (_currentInvocations)
        {
            _currentInvocations.Remove(message.InvocationId);
        }

        state.Result = message;

        switch (message.State)
        {
            //case InvocationProxyResultMessage.StateType.RequestCancel:
            //    state.TrySetCanceled();
            //    break;
            case InvocationResultMessage.StateType.CompletedResult:
                state.TrySetResult();
                break;
            case InvocationResultMessage.StateType.Exception:
                state.TrySetException(new InvocationException(message.InvocationId));
                break;

            default:
                state.TrySetException(new Exception("Invalid state returned."));
                break;

        }
    }

    public async ValueTask<RegisteredInvocationState?> InvokeMethodWithResultCore(
        ushort methodId,
        ITuple? arguments,
        INexusSession session,
        CancellationToken? cancellationToken = null)
    {
        if (cancellationToken?.IsCancellationRequested == true)
            return null;

        using var message = _cacheManager.Rent<InvocationMessage>();

        message.InvocationId = GetNextId(true);
        message.MethodId = methodId;
        message.Flags = InvocationFlags.None;

        // Try to set the arguments. If we can not, then the arguments are too large.
        if (!message.TrySetArguments(arguments))
        {
            throw new ArgumentOutOfRangeException($"Message arguments exceeds maximum size allowed Must be {IInvocationMessage.MaxArgumentSize} bytes or less.");
        }

        var state = _cacheManager.RegisteredInvocationStateCache.Rent();

        state.InvocationId = message.InvocationId;
        // Reset state information for this invocation.

        if (cancellationToken != null)
        {
            static void Callback(object? arg)
            {
                var state = (RegisteredInvocationState)arg!;
                state.TrySetCanceled(true);
            }

            cancellationToken.Value.Register(Callback, state);
        }

        state.Created = Environment.TickCount64;

        // Add the state information to the active states.

        if (cancellationToken == null || cancellationToken?.IsCancellationRequested == false)
        {
            _invocationStates.TryAdd(message.InvocationId, state);

            await session.SendMessage(message).ConfigureAwait(false);
        }
        else
        {
            _cacheManager.RegisteredInvocationStateCache.Return(state);
            return null;
        }

        // Return the message to the cache
        return state;
    }

    public void CancelAll()
    {
        foreach (var invocationState in _invocationStates)
            invocationState.Value.TrySetCanceled(false);

        _invocationStates.Clear();
        _currentInvocations.Clear();

        //foreach (var invocationState in _waitingPipes)
        //    invocationState.Value.Pipe.UpstreamComplete(PipeCompleteMessage.Flags.Canceled);
    }
}
