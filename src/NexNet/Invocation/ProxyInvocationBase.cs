using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Messages;
using static NexNet.Messages.InvocationProxyResultMessage;

namespace NexNet.Invocation;

public abstract class ProxyInvocationBase : IProxyInvoker
{

    private CacheManager _cacheManager = null!;
    private ProxyInvocationMode _mode;
    private long[]? _modeClientArguments;
    private string[]? _modeGroupArguments;
    private INexNetSession _session;
    

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
            case ProxyInvocationMode.Clients:
                _modeClientArguments = Unsafe.As<long[]>(modeArguments!);
                break;

            case ProxyInvocationMode.AllExcept:
                _modeClientArguments = Unsafe.As<long[]>(modeArguments!);
                break;

            //case ProxyInvocationMode.Client:
            //    _modeClientArguments = Unsafe.As<long[]>(modeArguments!);
            //    break;
            //
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



    protected async ValueTask InvokeMethod(ushort methodId, byte[]? arguments)
    {
        var message = _cacheManager.InvocationRequestDeserializer.Rent();
        message.MethodId = methodId;
        message.Arguments = arguments;
        message.Flags = InvocationRequestMessage.InvocationFlags.IgnoreReturn;
        message.InvocationId = 0;

        switch (_mode)
        {
            case ProxyInvocationMode.Caller:
            {
                await _session.SendHeaderWithBody(message);
                break;
            }
            case ProxyInvocationMode.All:
            {
                foreach (var (id, session) in _session.SessionManager!.Sessions)
                {
                    message.InvocationId = session.SessionInvocationStateManager.GetNextId();
                    await session.SendHeaderWithBody(message);
                }

                break;
            }
            case ProxyInvocationMode.Others:
            {
                foreach (var (id, session) in _session.SessionManager!.Sessions)
                {
                    if (id == _session.Id)
                        continue;

                    await session.SendHeaderWithBody(message);
                }

                break;
            }
            case ProxyInvocationMode.Clients:
            {
                for (int i = 0; i < _modeClientArguments.Length; i++)
                {
                    if (_session.SessionManager!.Sessions.TryGetValue(_modeClientArguments[i], out var session))
                        await session.SendHeaderWithBody(message);
                }

                break;
            }
            //case ProxyInvocationMode.Client:
            //{
            //    if (_sessionManager!.Sessions.TryGetValue(_modeClientArguments[0], out var session))
            //        session.Channel.SendHeaderWithBody(message);
            //    break;
            //}
            case ProxyInvocationMode.AllExcept:
            {
                foreach (var (id, session) in _session.SessionManager!.Sessions)
                {
                    if (id == _modeClientArguments[0])
                        continue;

                    await session.SendHeaderWithBody(message);
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
                for (int i = 0; i < _modeGroupArguments.Length; i++)
                {
                    await _session.SessionManager!.GroupChannelIterator(_modeGroupArguments[i], static async (session, message) =>
                    {
                        await session.SendHeaderWithBody(message);
                    }, message);
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException();
        }

        // Return the message to the cache
        _cacheManager.InvocationRequestDeserializer.Return(message);
    }


    private void ReturnState(RegisteredInvocationState state)
    {
        _cacheManager.InvocationProxyResultDeserializer.Return(state.Result);
        state.Result = null!;
        _cacheManager.RegisteredInvocationStateCache.Return(state);
    }

    protected async ValueTask InvokeWaitForResult(ushort methodId, byte[]? arguments, CancellationToken? cancellationToken = null)
    {
        // If we are invoking on multiple sessions, then we are not going to wait
        // on the results on this proxy invocation.
        if (_mode != ProxyInvocationMode.Caller)
        {
            await InvokeMethod(methodId, arguments);
            return;
        }

        var state = await _session.SessionInvocationStateManager.InvokeMethodWithResultCore(methodId, arguments, _session, cancellationToken);

        if (state == null)
            return;

        try
        {
            if (cancellationToken?.IsCancellationRequested == true)
                return;

            await new ValueTask<bool>(state, state.Version);

            if (state.IsCanceled)
            {
                var message = CacheManager.InvocationCancellationRequestDeserializer.Rent();
                message.InvocationId = state.InvocationId;
                _session?.SendHeaderWithBody(message);
                CacheManager.InvocationCancellationRequestDeserializer.Return(message);

                ReturnState(state);
                return;
            }

        }
        catch (Exception e)
        {
            //noop
        }

        switch (state.Result.State)
        {
            case StateType.CompletedResult:
                return;

            case StateType.Exception:
                throw new ProxyRemoteInvocationException();
                break;

            default:
                throw new InvalidOperationException($"Unknown state {state.Result.State}");
        }

        ReturnState(state);
    }

    protected async ValueTask<TReturn?> InvokeWaitForResult<TReturn>(ushort methodId, byte[]? arguments, CancellationToken? cancellationToken = null)
    {
        // If we are invoking on multiple sessions, then we are not going to wait
        // on the results on this proxy invocation.
        if (_mode != ProxyInvocationMode.Caller)
        {
            await InvokeMethod(methodId, arguments);
            return default;
        }

        var state = await _session.SessionInvocationStateManager.InvokeMethodWithResultCore(methodId, arguments, _session, cancellationToken);

        if (state == null)
            return default;

        try
        {
            if (cancellationToken?.IsCancellationRequested == true)
                return default;

            await new ValueTask<bool>(state, state.Version).ConfigureAwait(false);

            if (state.IsCanceled)
            {
                if (state.NotifyConnection)
                {
                    var message = CacheManager.InvocationCancellationRequestDeserializer.Rent();
                    message.InvocationId = state.InvocationId;
                    _session?.SendHeaderWithBody(message);
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
        catch (Exception e)
        {
            //noop
        }

        switch (state.Result.State)
        {
            case StateType.CompletedResult:
                var result = state.Result.GetResult<TReturn>();
                ReturnState(state);
                return result;

            case StateType.Exception:
                ReturnState(state);
                throw new ProxyRemoteInvocationException();
                break;

            default:
                ReturnState(state);
                throw new InvalidOperationException($"Unknown state {state.Result.State}");
        }

        return default;

    }
}
