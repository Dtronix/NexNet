using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections.Lists;
using NexNet.Pools;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Invocation;

/// <summary>
/// Base for proxy invocations and handling of return and cancellations.
/// </summary>
public abstract class ProxyInvocationBase : IProxyInvoker
{
    private const string CanNotInvokeDuplexPipeMessage = $"Can not invoke method with {nameof(INexusDuplexPipe)} on multiple connections";
    private const string SessionManagerNullMessage = "Session manager is null where it should not be.  Usually an indication that a server invocation is being attempted on the client.";

    private PoolManager _poolManager = null!;
    private ProxyInvocationMode _mode;
    private long[]? _modeClientArguments;
    private string[]? _modeGroupArguments;
    private INexusSession? _session;
    private IServerSessionManager? _sessionManager;
    private IInvocationRouter? _invocationRouter;

    internal PoolManager PoolManager
    {
        get => _poolManager;
        set => _poolManager = value;
    }

    /// <inheritdoc />
    INexusLogger? IProxyInvoker.Logger => _session?.Logger;

    void IProxyInvoker.Configure(
        INexusSession? session,
        IServerSessionManager? sessionManager,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        _session = session;

        // Sets all the proxy session required configurations for all clients.
        if(_session?.IsServer == false)
            session?.CollectionManager.SetClientProxySession(this, session);

        // If the sessionManager is null, this is a client session.
        _sessionManager = sessionManager;
        _invocationRouter = sessionManager?.Router;
        _mode = mode;

        switch (mode)
        {
            case ProxyInvocationMode.Client:
                _modeClientArguments = Unsafe.As<long[]>(modeArguments!);
                break;

            case ProxyInvocationMode.Clients:
                _modeClientArguments = Unsafe.As<long[]>(modeArguments!);
                break;

            case ProxyInvocationMode.AllExcept:
                _modeClientArguments = Unsafe.As<long[]>(modeArguments!);
                break;
            
            //case ProxyInvocationMode.Group:
            //    _modeGroupArguments = Unsafe.As<string[]>(modeArguments!);
            //    break;

            case ProxyInvocationMode.Groups:
            case ProxyInvocationMode.GroupsExceptCaller:
                _modeGroupArguments = Unsafe.As<string[]>(modeArguments!);
                break;

            case ProxyInvocationMode.Caller:
                // No arguments.
                break;

            case ProxyInvocationMode.Others:
                // No arguments.
                break;

            case ProxyInvocationMode.All:
                // No arguments.
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    /// <inheritdoc />
    ValueTask IProxyInvoker.ProxyInvokeMethodCore(ushort methodId, Memory<byte> serializedArguments, InvocationFlags flags)
        => ProxyInvokeMethodCoreInternal(methodId, serializedArguments, flags, null);

    /// <inheritdoc />
    ValueTask IProxyInvoker.ProxyInvokeMethodCore(ushort methodId, PooledArrayBufferWriter serializedArguments, InvocationFlags flags)
        => ProxyInvokeMethodCoreInternal(methodId, serializedArguments.WrittenMemory, flags, serializedArguments);

    private async ValueTask ProxyInvokeMethodCoreInternal(ushort methodId, Memory<byte> serializedArguments, InvocationFlags flags, PooledArrayBufferWriter? argumentsOwner)
    {
        // Verify if we are on the server or client.  Server will use the _sessionManager and the client will use _session.
        if (_sessionManager == null && _session?.State != ConnectionState.Connected)
        {
            argumentsOwner?.Return();
            throw new InvalidOperationException("Session is not connected");
        }

        var message = _poolManager.Rent<InvocationMessage>();
        message.ArgumentsOwner = argumentsOwner;
        message.MethodId = methodId;
        message.Flags = InvocationFlags.IgnoreReturn | flags;
        message.InvocationId = 0;
        message.SetArguments(serializedArguments);

        switch (_mode)
        {
            case ProxyInvocationMode.Caller:
            {
                var session = _session!;
                if (serializedArguments.Length > session.SendMaxMessageSize)
                {
                    message.Dispose();
                    throw new NexusMessageTooLargeException(serializedArguments.Length, session.SendMaxMessageSize);
                }

                // An argument larger than one frame is streamed under its invocation ID, so it needs a tracked one
                // even though no result comes back.
                var streamed = serializedArguments.Length > IInvocationMessage.MaxArgumentSize;
                if (streamed)
                    message.InvocationId = session.SessionInvocationStateManager.GetNextId(true);

                try
                {
                    await session.SendInvocation(message).ConfigureAwait(false);
                }
                finally
                {
                    if (streamed)
                        session.SessionInvocationStateManager.ReleaseId(message.InvocationId);
                }

                break;
            }
            case ProxyInvocationMode.All:
            {
                if ((flags & InvocationFlags.DuplexPipe) != 0)
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeAllAsync(message).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.Others:
            {
                if ((flags & InvocationFlags.DuplexPipe) != 0)
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeAllExceptAsync(message, _session!.Id).ConfigureAwait(false);
                break;
            }

            case ProxyInvocationMode.Clients:
            {
                if ((flags & InvocationFlags.DuplexPipe) != 0)
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeClientsAsync(message, _modeClientArguments!).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.Client:
            {
                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeClientAsync(message, _modeClientArguments![0]).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.AllExcept:
            {
                if ((flags & InvocationFlags.DuplexPipe) != 0)
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeAllExceptAsync(message, _modeClientArguments![0]).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.Groups:
            {
                if ((flags & InvocationFlags.DuplexPipe) != 0)
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeGroupsAsync(message, _modeGroupArguments!, null).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.GroupsExceptCaller:
            {
                if ((flags & InvocationFlags.DuplexPipe) != 0)
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_invocationRouter == null)
                    throw new InvalidOperationException(SessionManagerNullMessage);

                await _invocationRouter.InvokeGroupsAsync(message, _modeGroupArguments!, _session?.Id).ConfigureAwait(false);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }

        // Return the message to the cache
        message.Dispose();
    }

    /// <inheritdoc />
    ValueTask IProxyInvoker.ProxyInvokeAndWaitForResultCore(ushort methodId, Memory<byte> serializedArguments, CancellationToken? cancellationToken)
        => ProxyInvokeAndWaitForResultCoreInternal(methodId, serializedArguments, cancellationToken, null);

    /// <inheritdoc />
    ValueTask IProxyInvoker.ProxyInvokeAndWaitForResultCore(ushort methodId, PooledArrayBufferWriter serializedArguments, CancellationToken? cancellationToken)
        => ProxyInvokeAndWaitForResultCoreInternal(methodId, serializedArguments.WrittenMemory, cancellationToken, serializedArguments);

    // A call that waits for its result returns a ValueTask backed by the pooled invocation state: when the
    // session is known and the send completes synchronously (the usual case), nothing is allocated for the call.
    private ValueTask ProxyInvokeAndWaitForResultCoreInternal(ushort methodId, Memory<byte> serializedArguments, CancellationToken? cancellationToken, PooledArrayBufferWriter? argumentsOwner)
    {
        INexusSession? session;
        ValueTask<INexusSession?> lookup;
        ResultTarget target;
        try
        {
            target = GetResultTarget(argumentsOwner, out session, out lookup);
        }
        catch (Exception e)
        {
            return ValueTask.FromException(e);
        }

        if (target == ResultTarget.Sessions)
            return ProxyInvokeMethodCoreInternal(methodId, serializedArguments, InvocationFlags.None, argumentsOwner);

        if (target == ResultTarget.Lookup)
            return InvokeAfterLookup(lookup, methodId, serializedArguments, cancellationToken, argumentsOwner);

        var state = RegisteredInvocationState.Rent();
        var start = session!.SessionInvocationStateManager.InvokeMethodWithResultCore(
            state, methodId, serializedArguments, session, cancellationToken, argumentsOwner);

        if (!start.IsCompletedSuccessfully)
            return AwaitStart(start, state);

        // Not started: canceled before it was sent, which completes the call without a result.
        return start.Result ? new ValueTask(state, state.Version) : default;
    }

    private static async ValueTask AwaitStart(ValueTask<bool> start, RegisteredInvocationState state)
    {
        if (await start.ConfigureAwait(false))
            await new ValueTask(state, state.Version).ConfigureAwait(false);
    }

    private async ValueTask InvokeAfterLookup(ValueTask<INexusSession?> lookup, ushort methodId, Memory<byte> serializedArguments, CancellationToken? cancellationToken, PooledArrayBufferWriter? argumentsOwner)
    {
        var session = await lookup.ConfigureAwait(false) ?? throw ClientNotFound(argumentsOwner);
        var state = RegisteredInvocationState.Rent();
        if (await session.SessionInvocationStateManager.InvokeMethodWithResultCore(
                state, methodId, serializedArguments, session, cancellationToken, argumentsOwner).ConfigureAwait(false))
            await new ValueTask(state, state.Version).ConfigureAwait(false);
    }

    /// <inheritdoc />
    ValueTask<TReturn> IProxyInvoker.ProxyInvokeAndWaitForResultCore<TReturn>(ushort methodId, Memory<byte> serializedArguments, CancellationToken? cancellationToken)
        => ProxyInvokeAndWaitForResultCoreInternal<TReturn>(methodId, serializedArguments, cancellationToken, null);

    /// <inheritdoc />
    ValueTask<TReturn> IProxyInvoker.ProxyInvokeAndWaitForResultCore<TReturn>(ushort methodId, PooledArrayBufferWriter serializedArguments, CancellationToken? cancellationToken)
        => ProxyInvokeAndWaitForResultCoreInternal<TReturn>(methodId, serializedArguments.WrittenMemory, cancellationToken, serializedArguments);

    private ValueTask<TReturn> ProxyInvokeAndWaitForResultCoreInternal<TReturn>(ushort methodId, Memory<byte> serializedArguments, CancellationToken? cancellationToken, PooledArrayBufferWriter? argumentsOwner)
    {
        INexusSession? session;
        ValueTask<INexusSession?> lookup;
        ResultTarget target;
        try
        {
            target = GetResultTarget(argumentsOwner, out session, out lookup);
        }
        catch (Exception e)
        {
            return ValueTask.FromException<TReturn>(e);
        }

        if (target == ResultTarget.Sessions)
            return InvokeOnSessions<TReturn>(methodId, serializedArguments, argumentsOwner);

        if (target == ResultTarget.Lookup)
            return InvokeAfterLookup<TReturn>(lookup, methodId, serializedArguments, cancellationToken, argumentsOwner);

        var state = RegisteredInvocationState<TReturn>.Rent();
        var start = session!.SessionInvocationStateManager.InvokeMethodWithResultCore(
            state, methodId, serializedArguments, session, cancellationToken, argumentsOwner);

        if (!start.IsCompletedSuccessfully)
            return AwaitStart(start, state);

        // Not started: canceled before it was sent, which returns the default value.
        return start.Result ? new ValueTask<TReturn>(state, state.Version) : default;
    }

    private static async ValueTask<TReturn> AwaitStart<TReturn>(ValueTask<bool> start, RegisteredInvocationState<TReturn> state)
    {
        if (!await start.ConfigureAwait(false))
            return default!;

        return await new ValueTask<TReturn>(state, state.Version).ConfigureAwait(false);
    }

    private async ValueTask<TReturn> InvokeAfterLookup<TReturn>(ValueTask<INexusSession?> lookup, ushort methodId, Memory<byte> serializedArguments, CancellationToken? cancellationToken, PooledArrayBufferWriter? argumentsOwner)
    {
        var session = await lookup.ConfigureAwait(false) ?? throw ClientNotFound(argumentsOwner);
        var state = RegisteredInvocationState<TReturn>.Rent();
        if (!await session.SessionInvocationStateManager.InvokeMethodWithResultCore(
                state, methodId, serializedArguments, session, cancellationToken, argumentsOwner).ConfigureAwait(false))
            return default!;

        return await new ValueTask<TReturn>(state, state.Version).ConfigureAwait(false);
    }

    private async ValueTask<TReturn> InvokeOnSessions<TReturn>(ushort methodId, Memory<byte> serializedArguments, PooledArrayBufferWriter? argumentsOwner)
    {
        await ProxyInvokeMethodCoreInternal(methodId, serializedArguments, InvocationFlags.None, argumentsOwner)
            .ConfigureAwait(false);

        // Sent to every session, but there is no single result to return.
        throw new InvalidOperationException("A method that returns a value can not be invoked on several sessions.");
    }

    /// <summary>
    /// Where a call that waits for its result goes.
    /// </summary>
    private enum ResultTarget
    {
        // The proxy's session, or a client session found at once.
        Session,

        // Several sessions: the call is sent without waiting for results.
        Sessions,

        // A client session that is still being looked up.
        Lookup
    }

    private ResultTarget GetResultTarget(PooledArrayBufferWriter? argumentsOwner, out INexusSession? session, out ValueTask<INexusSession?> lookup)
    {
        session = null;
        lookup = default;

        // Verify if we are on the server or client.  Server will use the _sessionManager and the client will use _session.
        if (_sessionManager == null && _session?.State != ConnectionState.Connected)
        {
            argumentsOwner?.Return();
            throw new InvalidOperationException("Session is not connected");
        }

        // If we are invoking on multiple sessions, then we are not going to wait
        // on the results on this proxy invocation.
        if (_mode == ProxyInvocationMode.All
            || _mode == ProxyInvocationMode.Groups
            || _mode == ProxyInvocationMode.GroupsExceptCaller
            || _mode == ProxyInvocationMode.AllExcept
            || _mode == ProxyInvocationMode.Clients
            || _mode == ProxyInvocationMode.Others)
            return ResultTarget.Sessions;

        if (_mode != ProxyInvocationMode.Client)
        {
            session = _session!;
            return ResultTarget.Session;
        }

        // Get the specific client if we are invoking on it.
        if (_sessionManager == null)
        {
            argumentsOwner?.Return();
            throw new ArgumentNullException(nameof(_sessionManager), SessionManagerNullMessage);
        }

        lookup = _sessionManager.Sessions.GetSessionAsync(_modeClientArguments![0]);
        if (!lookup.IsCompletedSuccessfully)
            return ResultTarget.Lookup;

        session = lookup.Result ?? throw ClientNotFound(argumentsOwner);
        return ResultTarget.Session;
    }

    private InvalidOperationException ClientNotFound(PooledArrayBufferWriter? argumentsOwner)
    {
        argumentsOwner?.Return();
        return new InvalidOperationException($"Can't invoke on client {_modeClientArguments![0]} as it does not exist.");
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
     byte IProxyInvoker.ProxyGetDuplexPipeInitialId(INexusDuplexPipe? pipe)
     {
         if (_session?.State != ConnectionState.Connected)
             throw new InvalidOperationException("Session is not connected");
         
         ArgumentNullException.ThrowIfNull(pipe);
         var nexusPipe = Unsafe.As<NexusDuplexPipe>(pipe);

         if(nexusPipe.InitiatingPipe == false)
             throw new InvalidOperationException(
                 "Pipe is not from initiating side of the invocation. Usually this means the proxy was passed a pipe which is already open on another invocation. Pipes can only be used once.");

         if(_session != nexusPipe.Session)
             throw new InvalidOperationException("Passed pipe from non-initiating side of duplex pipe.  Usually means that a server pipe was passed to a client proxy or vice versa.");

         if(nexusPipe.CurrentState != NexusDuplexPipe.State.Unset)
             throw new InvalidOperationException("Pipe is already open on another invocation. Pipes can only be used once.");

         return nexusPipe.LocalId;
     }


    /// <inheritdoc />
    public INexusList<T> ProxyGetConfiguredNexusList<T>(ushort id)
    {
        var session = _session;
        if (session == null)
            throw new InvalidOperationException("Method must be invoked on client only.");
            
        // Verify if we are on the server or client.  Client will use _session.
        if (session.State != ConnectionState.Connected)
            throw new InvalidOperationException("Session is not connected");
        
        var list = session.CollectionManager.GetList<T>(id);
        return list;
    }
}
