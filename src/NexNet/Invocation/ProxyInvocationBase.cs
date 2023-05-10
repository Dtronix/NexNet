using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Invocation;

/// <summary>
/// Base for proxy invocations and handling of return and cancellations.
/// </summary>
public abstract class ProxyInvocationBase : IProxyInvoker
{

    private CacheManager _cacheManager = null!;
    private ProxyInvocationMode _mode;
    private long[]? _modeClientArguments;
    private string[]? _modeGroupArguments;
    private INexNetSession _session = null!;
    

    internal CacheManager CacheManager
    {
        get => _cacheManager;
        set => _cacheManager = value;
    }

    void IProxyInvoker.Configure(
        INexNetSession session,
        ProxyInvocationMode mode,
        object? modeArguments)
    {
        _session = session;
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
    /// Checks the passed data and serializes the data into a byte array. 
    /// </summary>
    /// <typeparam name="T">Data type to serialize.</typeparam>
    /// <param name="data">Data to serialize.</param>
    /// <returns>Serialized data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Throws if the serialized data exceeds the maximum message length.</exception>
    protected byte[] SerializeArgumentsCore<T>(in T data)
    {
        var arguments = MemoryPackSerializer.Serialize<T>(data);

        // Check for arguments which exceed max length.
        if (arguments.Length > IInvocationRequestMessage.MaxArgumentSize)
            throw new ArgumentOutOfRangeException(nameof(arguments), arguments.Length, $"Message arguments exceeds maximum size allowed Must be {NexNet.Messages.IInvocationRequestMessage.MaxArgumentSize} bytes or less.");

        return arguments;
    }

    /// <summary>
    /// Invokes the specified method on the connected session and waits until the message has been completely sent.
    /// Will not wait for results on invocations and will instruct the proxy to dismiss any results.
    /// </summary>
    /// <param name="methodId">Method ID to invoke.</param>
    /// <param name="arguments">Optional arguments to pass to the method invocation.</param>
    /// <returns>Task which returns when the invocations messages have been issued.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the invocation mode is set in an invalid mode.</exception>
    protected async ValueTask ProxyInvokeMethodCore(ushort methodId, byte[]? arguments)
    {
        var message = _cacheManager.InvocationRequestDeserializer.Rent();
        message.MethodId = methodId;
        message.Arguments = arguments;
        message.Flags = InvocationFlags.IgnoreReturn;
        message.InvocationId = 0;

        switch (_mode)
        {
            case ProxyInvocationMode.Caller:
            {
                await _session.SendHeaderWithBody(message).ConfigureAwait(false);
                break;
            }
            case ProxyInvocationMode.All:
            {
                foreach (var (_, session) in _session.SessionManager!.Sessions)
                {
                    message.InvocationId = session.SessionInvocationStateManager.GetNextId();
                    try
                    {
                        await session.SendHeaderWithBody(message).ConfigureAwait(false);
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
                foreach (var (id, session) in _session.SessionManager!.Sessions)
                {
                    if (id == _session.Id)
                        continue;

                    try
                    {
                        await session.SendHeaderWithBody(message).ConfigureAwait(false);
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
                for (int i = 0; i < _modeClientArguments!.Length; i++)
                {
                    if (_session.SessionManager!.Sessions.TryGetValue(_modeClientArguments[i], out var session))
                        try
                        {
                            await session.SendHeaderWithBody(message).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Don't care if we can't invoke on another session here.
                        }
                }
                throw new ArgumentOutOfRangeException(nameof(arguments), arguments.Length, $"Message arguments exceeds maximum size allowed Must be {NexNet.Messages.IInvocationRequestMessage.MaxArgumentSize} bytes or less.");
                    break;
            }
            case ProxyInvocationMode.Client:
            {
                if (_session.SessionManager!.Sessions.TryGetValue(_modeClientArguments![0], out var session))
                {
                    try
                    {
                        await session.SendHeaderWithBody(message).ConfigureAwait(false);
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
                foreach (var (id, session) in _session.SessionManager!.Sessions)
                {
                    if (id == _modeClientArguments![0])
                        continue;

                    try
                    {
                        await session.SendHeaderWithBody(message).ConfigureAwait(false);
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
                for (int i = 0; i < _modeGroupArguments!.Length; i++)
                {
                    await _session.SessionManager!.GroupChannelIterator(_modeGroupArguments[i], static async (session, message) =>
                    {
                        try
                        {
                            await session.SendHeaderWithBody(message).ConfigureAwait(false);
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
        _cacheManager.InvocationRequestDeserializer.Return(message);
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
    protected async ValueTask ProxyInvokeAndWaitForResultCore(ushort methodId, byte[]? arguments, CancellationToken? cancellationToken = null)
    {
        var state = await InvokeWaitForResultCore(methodId, arguments, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return;

        var messageState = state.Result.State;
        ReturnState(state);

        switch (messageState)
        {
            case InvocationProxyResultMessage.StateType.CompletedResult:
                return;

            case InvocationProxyResultMessage.StateType.Exception:
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
    protected async ValueTask<TReturn?> ProxyInvokeAndWaitForResultCore<TReturn>(ushort methodId, byte[]? arguments, CancellationToken? cancellationToken = null)
    {
        var state = await InvokeWaitForResultCore(methodId, arguments, cancellationToken).ConfigureAwait(false);

        if (state == null)
            return default;

        switch (state.Result.State)
        {
            case InvocationProxyResultMessage.StateType.CompletedResult:
                var result = state.Result.GetResult<TReturn>();
                ReturnState(state);
                return result;

            case InvocationProxyResultMessage.StateType.Exception:
                ReturnState(state);
                throw new ProxyRemoteInvocationException();

            default:
                ReturnState(state);
                throw new InvalidOperationException($"Unknown state {state.Result.State}");
        }
    }

    private void ReturnState(RegisteredInvocationState state)
    {
        _cacheManager.InvocationProxyResultDeserializer.Return(state.Result);
        state.Result = null!;
        _cacheManager.RegisteredInvocationStateCache.Return(state);
    }

    private async ValueTask<RegisteredInvocationState?> InvokeWaitForResultCore(
        ushort methodId, 
        byte[]? arguments,
        CancellationToken? cancellationToken = null)
    {
        // If we are invoking on multiple sessions, then we are not going to wait
        // on the results on this proxy invocation.
        if (_mode != ProxyInvocationMode.Caller && _mode != ProxyInvocationMode.Client)
        {
            await ProxyInvokeMethodCore(methodId, arguments).ConfigureAwait(false);
            return null;
        }

        var session = _session;
        if (_mode == ProxyInvocationMode.Client)
        {
            _session.SessionManager!.Sessions.TryGetValue(_modeClientArguments![0], out session);

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
                    var message = CacheManager.InvocationCancellationRequestDeserializer.Rent();
                    message.InvocationId = state.InvocationId;
                    await session.SendHeaderWithBody(message).ConfigureAwait(false);
                    CacheManager.InvocationCancellationRequestDeserializer.Return(message);
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
