using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Collections.Lists;
using NexNet.Internals;
using NexNet.Logging;
using NexNet.Messages;
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

    private CacheManager _cacheManager = null!;
    private ProxyInvocationMode _mode;
    private long[]? _modeClientArguments;
    private string[]? _modeGroupArguments;
    private INexusSession? _session;
    private SessionManager? _sessionManager;

    internal CacheManager CacheManager
    {
        get => _cacheManager;
        set => _cacheManager = value;
    }

    /// <inheritdoc />
    INexusLogger? IProxyInvoker.Logger => _session?.Logger;

    void IProxyInvoker.Configure(
        INexusSession? session,
        SessionManager? sessionManager,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        _session = session;
        
        // Sets all the proxy session required configurations for all clients.
        session?.CollectionManager.SetClientProxySession(this, session);

        // If the sessionManager is null, this is a client session.
        _sessionManager = sessionManager;
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
    async Task IProxyInvoker.ProxyInvokeMethodCore(ushort methodId, ITuple? arguments, InvocationFlags flags)
    {
        // Verify if we are on the server or client.  Server will use the _sessionManager and the client will use _session.
        if (_sessionManager == null && _session?.State != ConnectionState.Connected)
            throw new InvalidOperationException("Session is not connected");
        
        var message = _cacheManager.Rent<InvocationMessage>();
        message.MethodId = methodId;
        message.Flags = InvocationFlags.IgnoreReturn | flags;
        message.InvocationId = 0;

        // Try to set the arguments. If we can not, then the arguments are too large.
        if (!message.TrySetArguments(arguments))
        {
            message.Dispose();
            throw new ArgumentOutOfRangeException($"Message arguments exceeds maximum size allowed Must be {IInvocationMessage.MaxArgumentSize} bytes or less.");
        }

        switch (_mode)
        {
            case ProxyInvocationMode.Caller:
            {
                await _session!.SendMessage(message).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.All:
            {
                if (flags.HasFlag(InvocationFlags.DuplexPipe))
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager),
                        SessionManagerNullMessage);

                foreach (var (_, session) in _sessionManager.Sessions)
                {
                    message.InvocationId = session.SessionInvocationStateManager.GetNextId(false);
                    try
                    {
                        await session.SendMessage(message).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Don't care if we can't invoke on another session here.
                    }

                }

                break;
            }
            case ProxyInvocationMode.Others:
            {
                if (flags.HasFlag(InvocationFlags.DuplexPipe))
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager),
                        SessionManagerNullMessage);

                foreach (var (id, session) in _sessionManager.Sessions)
                {
                    if (id == _session!.Id)
                        continue;

                    try
                    {
                        await session.SendMessage(message).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Don't care if we can't invoke on another session here.
                    }
                }

                break;
            }


            case ProxyInvocationMode.Clients:
            {
                if (flags.HasFlag(InvocationFlags.DuplexPipe))
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager),
                        SessionManagerNullMessage);

                for (int i = 0; i < _modeClientArguments!.Length; i++)
                {
                    if (_sessionManager.Sessions.TryGetValue(_modeClientArguments[i], out var session))
                        try
                        {
                            await session.SendMessage(message).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Don't care if we can't invoke on another session here.
                        }
                }

                break;
            }
            case ProxyInvocationMode.Client:
            {
                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager),
                        SessionManagerNullMessage);

                if (_sessionManager.Sessions.TryGetValue(_modeClientArguments![0], out var session))
                {
                    try
                    {
                        await session.SendMessage(message).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Don't care if we can't invoke on another session here.
                    }
                }

                break;
            }
            case ProxyInvocationMode.AllExcept:
            {
                if (flags.HasFlag(InvocationFlags.DuplexPipe))
                    throw new InvalidOperationException(CanNotInvokeDuplexPipeMessage);

                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager), SessionManagerNullMessage);

                foreach (var (id, session) in _sessionManager.Sessions)
                {
                    if (id == _modeClientArguments![0])
                        continue;

                    try
                    {
                        await session.SendMessage(message).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Don't care if we can't invoke on another session here.
                    }
                }

                break;
            }
            //case ProxyInvocationMode.Group:
            //    _sessionManager!.GroupChannelIterator(_modeGroupArguments[0], static (channel, message) =>
            //    {
            //        channel.SendHeaderWithBody(message);
            //    }, message);
            //    break;
            case ProxyInvocationMode.Groups:
            case ProxyInvocationMode.GroupsExceptCaller:
            {
                if (flags.HasFlag(InvocationFlags.DuplexPipe))
                    throw new InvalidOperationException(
                        $"Can't invoke method with {nameof(INexusDuplexPipe)} on multiple connections");

                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager),
                        SessionManagerNullMessage);

                for (int i = 0; i < _modeGroupArguments!.Length; i++)
                {
                    await _sessionManager.GroupChannelIterator(
                        _modeGroupArguments[i],
                        static (session, message) => session.SendMessage(message),
                        message,
                        _mode == ProxyInvocationMode.GroupsExceptCaller ? _session?.Id : null).ConfigureAwait(false);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }

        // Return the message to the cache
        message.Dispose();
    }

    /// <inheritdoc />
    async Task IProxyInvoker.ProxyInvokeAndWaitForResultCore(ushort methodId, ITuple? arguments, CancellationToken? cancellationToken)
    {
        // Verify if we are on the server or client.  Server will use the _sessionManager and the client will use _session.
        if (_sessionManager == null && _session?.State != ConnectionState.Connected)
            throw new InvalidOperationException("Session is not connected");
        
        var state = await InvokeWaitForResultCore(methodId, arguments, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return;

        var messageState = state.Result!.State;
        ReturnState(state);

        switch (messageState)
        {
            case InvocationResultMessage.StateType.CompletedResult:
                return;

            case InvocationResultMessage.StateType.Exception:
                throw new ProxyRemoteInvocationException();

            default:
                throw new InvalidOperationException($"Unknown state {messageState}");
        }
    }

    /// <inheritdoc />
    async Task<TReturn> IProxyInvoker.ProxyInvokeAndWaitForResultCore<TReturn>(ushort methodId, ITuple? arguments, CancellationToken? cancellationToken)
    {
        // Verify if we are on the server or client.  Server will use the _sessionManager and the client will use _session.
        if (_sessionManager == null && _session?.State != ConnectionState.Connected)
            throw new InvalidOperationException("Session is not connected");
        
        var state = await InvokeWaitForResultCore(methodId, arguments, cancellationToken).ConfigureAwait(false);

        if (state == null)
        {
            if(cancellationToken?.IsCancellationRequested == true)
                return default!;

            throw new InvalidOperationException("Invocation state is null when it should not be.");
        }

        switch (state.Result!.State)
        {
            case InvocationResultMessage.StateType.CompletedResult:
                var success = state.Result.TryGetResult<TReturn>(out var result);
                ReturnState(state);

                if(!success)
                    throw new InvalidOperationException($"Could not get result of type {typeof(TReturn)} from invocation result.");

                return result!;

            case InvocationResultMessage.StateType.Exception:
                ReturnState(state);
                throw new ProxyRemoteInvocationException();

            default:
                ReturnState(state);
                throw new InvalidOperationException($"Unknown state {state.Result.State}");
        }
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


    private void ReturnState(RegisteredInvocationState state)
    {
        state.Result?.Dispose();

        state.Result = null;
        _cacheManager.RegisteredInvocationStateCache.Return(state);
    }

    private async ValueTask<RegisteredInvocationState?> InvokeWaitForResultCore(
        ushort methodId,
        ITuple? arguments,
        CancellationToken? cancellationToken = null)
    {
        // If we are invoking on multiple sessions, then we are not going to wait
        // on the results on this proxy invocation.
        if (_mode == ProxyInvocationMode.All
            || _mode == ProxyInvocationMode.Groups
            || _mode == ProxyInvocationMode.GroupsExceptCaller
            || _mode == ProxyInvocationMode.AllExcept
            || _mode == ProxyInvocationMode.Clients
            || _mode == ProxyInvocationMode.Others)
        {
            await Unsafe.As<IProxyInvoker>(this)
                .ProxyInvokeMethodCore(methodId, arguments, InvocationFlags.None)
                .ConfigureAwait(false);
            return null;
        }

        var session = _session!;

        // Get the specific client if we are invoking on it.
        if (_mode == ProxyInvocationMode.Client)
        {
            if (_sessionManager == null)
                throw new ArgumentNullException(nameof(_sessionManager),
                    SessionManagerNullMessage);

            _sessionManager.Sessions.TryGetValue(_modeClientArguments![0], out session);

            if (session == null)
                throw new InvalidOperationException(
                    $"Can't invoke on client {_modeClientArguments![0]} as it does not exist.");
        }

        var state = await session.SessionInvocationStateManager.InvokeMethodWithResultCore(
            methodId,
            arguments, 
            session, 
            cancellationToken).ConfigureAwait(false);

        if (state == null)
            return null;

        try
        {
            if (cancellationToken?.IsCancellationRequested == true)
            {
                ReturnState(state);
                return null;
            }

            await new ValueTask<bool>(state, state.Version).ConfigureAwait(false);
            if (state.IsCanceled)
            {
                if (state.NotifyConnection)
                {
                    using var message = CacheManager.Rent<InvocationCancellationMessage>();
                    message.InvocationId = state.InvocationId;
                    await session.SendMessage(message).ConfigureAwait(false);
                }

                ReturnState(state);
                throw new TaskCanceledException();
            }

        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            //noop
        }

        return state;
    }
}
