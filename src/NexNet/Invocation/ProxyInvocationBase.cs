using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Internals.Pipes;
using NexNet.Messages;

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

    void IProxyInvoker.Configure(
        INexusSession? session,
        SessionManager? sessionManager,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        _session = session;
        
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

    /// <summary>
    /// Invokes the specified method on the connected session and waits until the message has been completely sent.
    /// Will not wait for results on invocations and will instruct the proxy to dismiss any results.
    /// </summary>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation.</param>
    /// <param name="flags">Special flags for the invocation of this method.</param>
    /// <returns>Task which returns when the invocations messages have been issued.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the invocation mode is set in an invalid mode.</exception>
    protected async ValueTask __ProxyInvokeMethodCore(ushort methodId, ITuple? arguments, InvocationFlags flags)
    {
        var message = _cacheManager.Rent<InvocationMessage>();
        message.MethodId = methodId;
        message.Flags = InvocationFlags.IgnoreReturn | flags;
        message.InvocationId = 0;

        // Try to set the arguments. If we can not, then the arguments are too large.
        if (!message.TrySetArguments(arguments))
        {
            _cacheManager.Return(message);
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
                    message.InvocationId = session.SessionInvocationStateManager.GetNextId();
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
            {
                if (flags.HasFlag(InvocationFlags.DuplexPipe))
                    throw new InvalidOperationException(
                        $"Can't invoke method with {nameof(INexusDuplexPipe)} on multiple connections");

                if (_sessionManager == null)
                    throw new ArgumentNullException(nameof(_sessionManager),
                        SessionManagerNullMessage);

                for (int i = 0; i < _modeGroupArguments!.Length; i++)
                {
                    await _sessionManager.GroupChannelIterator(_modeGroupArguments[i],
                        static async (session, message) =>
                        {
                            try
                            {
                                await session.SendMessage(message).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Don't care if we can't invoke on another session here.
                            }
                        }, message).ConfigureAwait(false);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }

        // Return the message to the cache
        _cacheManager.Return(message);
    }

    /// <summary>
    /// Invokes a method ID on the connection with the optionally passed arguments and optional cancellation token
    /// and waits the the completion of the invocation.
    /// </summary>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation</param>
    /// <param name="cancellationToken">Optional cancellation token to allow cancellation of remote invocation.</param>
    /// <returns>ValueTask which completes upon remote invocation completion.</returns>
    /// <exception cref="ProxyRemoteInvocationException">Throws this exception if the remote invocation threw an exception.</exception>
    /// <exception cref="InvalidOperationException">Invocation returned invalid state data upon completion.</exception>
    protected async ValueTask __ProxyInvokeAndWaitForResultCore(ushort methodId, ITuple? arguments, CancellationToken? cancellationToken = null)
    {
        var state = await InvokeWaitForResultCore(methodId, arguments, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return;

        var messageState = state.Result.State;
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

    /// <summary>
    /// Invokes a method ID on the connection with the optionally passed arguments and optional cancellation token,
    /// waits the the completion of the invocation and returns the value of the invocation.
    /// </summary>
    /// <typeparam name="TReturn">Expected type to be returned by the remote invocation proxy.</typeparam>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation</param>
    /// <param name="cancellationToken">Optional cancellation token to allow cancellation of remote invocation.</param>
    /// <returns>ValueTask with the containing return result which completes upon remote invocation completion.</returns>
    /// <exception cref="ProxyRemoteInvocationException">Throws this exception if the remote invocation threw an exception.</exception>
    /// <exception cref="InvalidOperationException">Invocation returned invalid state data upon completion.</exception>
    protected async ValueTask<TReturn?> __ProxyInvokeAndWaitForResultCore<TReturn>(ushort methodId, ITuple? arguments, CancellationToken? cancellationToken = null)
    {
        var state = await InvokeWaitForResultCore(methodId, arguments, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return default;

        switch (state.Result.State)
        {
            case InvocationResultMessage.StateType.CompletedResult:
                var result = state.Result.GetResult<TReturn>();
                ReturnState(state);
                return result;

            case InvocationResultMessage.StateType.Exception:
                ReturnState(state);
                throw new ProxyRemoteInvocationException();

            default:
                ReturnState(state);
                throw new InvalidOperationException($"Unknown state {state.Result.State}");
        }
    }

    /// <summary>
     /// Gets the Initial Id of the duplex pipe.
     /// </summary>
     /// <param name="pipe">Pipe to retrieve the Id of.</param>
     /// <returns>Initial id of the pipe.</returns>
     [MethodImpl(MethodImplOptions.AggressiveInlining)]
     protected static byte __ProxyGetDuplexPipeInitialId(INexusDuplexPipe pipe)
     {
         return Unsafe.As<NexusDuplexPipe>(pipe).InitialId;
     }
     

    private void ReturnState(RegisteredInvocationState state)
    {
        if(state.Result != null)
            _cacheManager.Return(state.Result);

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
            || _mode == ProxyInvocationMode.AllExcept
            || _mode == ProxyInvocationMode.Clients
            || _mode == ProxyInvocationMode.Others)
        {
            await __ProxyInvokeMethodCore(methodId, arguments, InvocationFlags.None).ConfigureAwait(false);
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
                    var message = CacheManager.Rent<InvocationCancellationMessage>();
                    message.InvocationId = state.InvocationId;
                    await session.SendMessage(message).ConfigureAwait(false);
                    CacheManager.Return(message);
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
