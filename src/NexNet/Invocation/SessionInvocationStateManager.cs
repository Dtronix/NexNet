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
    private readonly INexusLogger? _logger;
    private int _invocationId = 0;

    private readonly ConcurrentDictionary<int, RegisteredInvocationState> _invocationStates;
    //private readonly ConcurrentDictionary<int, RegisteredNexusPipe> _waitingPipes;

    public SessionInvocationStateManager(CacheManager cacheManager, INexusLogger? logger)
    {
        _invocationStates = new ConcurrentDictionary<int, RegisteredInvocationState>();
        //_waitingPipes = new ConcurrentDictionary<int, RegisteredNexusPipe>();
        _cacheManager = cacheManager;
        _logger = logger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNextId()
    {
        // TODO: Review adding a list of currently invoked invocations so that when
        // we circle back around to the beginning and we have some long running invocation, we do not 
        // override or send data to it erroneously.
        return Interlocked.Increment(ref _invocationId);
    }

    public void UpdateInvocationResult(InvocationResultMessage message)
    {
        // If we can not remove the state any longer, then it has already been handled.
        if (!_invocationStates.TryRemove(message.InvocationId, out var state))
            return;

        state.Result = message;

        Console.WriteLine($"UpdateInvocationResult result: {message.GetResult<int>()}");

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

        message.InvocationId = GetNextId();
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
                //Console.WriteLine("Canceled");
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

        //foreach (var invocationState in _waitingPipes)
        //    invocationState.Value.Pipe.UpstreamComplete(PipeCompleteMessage.Flags.Canceled);
    }
}
