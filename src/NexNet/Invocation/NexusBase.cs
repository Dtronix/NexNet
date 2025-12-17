using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;

namespace NexNet.Invocation;

/// <summary>
/// Base hub with common methods used between server and client.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
public abstract class NexusBase<TProxy> : IMethodInvoker, ICollectionStore
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private delegate ValueTask InvokeMethodCoreDelegate(InvocationMessage message, IBufferWriter<byte>? returnBuffer);

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _cancellableInvocations = new();

    private readonly InvokeMethodCoreDelegate _invokeMethodCoreDelegate;
    private SessionContext<TProxy>? _sessionContext;

    internal SessionContext<TProxy> SessionContext
    {
        get => _sessionContext ?? throw new Exception("Session context is null");
        set
        {
            // Ensure that this is not accidentally set multiple times.
            if(_sessionContext != null && value.Session.Config is not ClientConfig)
                throw  new Exception("Session context is already set");
            
            _sessionContext = value;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusBase{TProxy}"/> class.
    /// </summary>
    protected NexusBase()
    {
        _invokeMethodCoreDelegate = InvokeMethodCore;
    }

    ValueTask IMethodInvoker.InvokeMethod(InvocationMessage message)
    {
        var args = InvokeMethodCoreArgs.Get();
        args.Message = message;
        args.SessionContext = SessionContext;
        args.InvokeMethodCore = _invokeMethodCoreDelegate;
        return InvokeMethodCoreTask(args);
    }

    CancellationTokenSource IMethodInvoker.RegisterCancellationToken(int invocationId)
    {
        var cts = SessionContext.CacheManager.CancellationTokenSourceCache.Rent();

        _cancellableInvocations.TryAdd(invocationId, cts);
        return cts;
    }

    void IMethodInvoker.ReturnCancellationToken(int invocationId)
    {
        if (!_cancellableInvocations.TryRemove(invocationId, out var cts))
            return;

        // Try to reset the cts for another operation.
        SessionContext.CacheManager.CancellationTokenSourceCache.Return(cts);
    }

    ValueTask<INexusDuplexPipe> IMethodInvoker.RegisterDuplexPipe(byte startId)
    {
        return SessionContext.Session.PipeManager.RegisterPipe(startId);
    }

    ValueTask IMethodInvoker.ReturnDuplexPipe(INexusDuplexPipe pipe)
    {
        return SessionContext.Session.PipeManager.DeregisterPipe(pipe);
    }

    void IMethodInvoker.CancelInvocation(InvocationCancellationMessage message)
    {
        if (!_cancellableInvocations.TryRemove(message.InvocationId, out var cts))
            return;

        cts.Cancel();
    }

    /// <summary>
    /// Method used to invoke 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="returnBuffer"></param>
    /// <returns></returns>
    protected abstract ValueTask InvokeMethodCore(IInvocationMessage message, IBufferWriter<byte>? returnBuffer);

    /// <summary>
    /// Invoked when a hub has it's connection established and ready for usage.
    /// </summary>
    /// <param name="isReconnected">True if the connection has been re-established after it has been lost.</param>
    /// <returns></returns>
    protected virtual ValueTask OnConnected(bool isReconnected)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Invoked when the hub has been disconnected.
    /// </summary>
    /// <param name="reason">Reason for the disconnection.</param>
    /// <returns></returns>
    protected virtual ValueTask OnDisconnected(DisconnectReason reason)
    {
        return ValueTask.CompletedTask;
    }

    internal ValueTask Connected(bool isReconnected)
    {
        return OnConnected(isReconnected);
    }

    /// <summary>
    /// Disconnects the hub and releases all the resources associated with it.
    /// </summary>
    internal void Disconnected(DisconnectReason reason)
    {
        OnDisconnected(reason);

        foreach (var cancellationTokenSource in _cancellableInvocations)
            cancellationTokenSource.Value.Dispose();

        _cancellableInvocations.Clear();
    }

    private static async ValueTask InvokeMethodCoreTask(InvokeMethodCoreArgs requestArgs)
    {
        var context = requestArgs.SessionContext;
        //LocalContext.Value = context;

        // If we have the flag to ignore the return value, then all we have to do is invoke and then
        // return the rest args to the cache.
        if (requestArgs.Message.Flags.HasFlag(InvocationFlags.IgnoreReturn))
        {
            await requestArgs.InvokeMethodCore(requestArgs.Message, null).ConfigureAwait(false);
            ReturnArgsToCache(context, requestArgs);
            return;
        }

        var message = context.CacheManager.Rent<InvocationResultMessage>();
        message.InvocationId = requestArgs.Message.InvocationId;

        try
        {
            var bufferWriterCache = requestArgs.SessionContext.CacheManager.BufferWriterCache;
            if (!bufferWriterCache.TryTake(out var bufferWriter))
                bufferWriter = BufferWriter<byte>.Create();

            await requestArgs.InvokeMethodCore(requestArgs.Message, bufferWriter).ConfigureAwait(false);
            Sequence<byte>? bufferResult = null;
            // If the length is zero, there is no return value.
            if (bufferWriter.Length == 0)
            {
                message.Result = null;
            }
            else
            {
                bufferResult = bufferWriter.GetBuffer();
                message.Result = bufferResult.Value;
            }

            message.State = InvocationResultMessage.StateType.CompletedResult;
            await context.Session.SendMessage(message).ConfigureAwait(false);

            if (bufferResult != null)
                bufferWriter.Deallocate(bufferResult.Value);

            bufferWriterCache.Add(bufferWriter);
        }
        catch (TaskCanceledException)
        {
            context.Session.Logger?.LogTrace("Invocation canceled.");
            // noop
        }
        catch (Exception e)
        {
            context.Session.Logger?.LogError(e, "Exception occurred while running the method.");
            message.Result = null;
            message.State = InvocationResultMessage.StateType.Exception;
            await context.Session.SendMessage(message).ConfigureAwait(false);
        }
        finally
        {
            message.Result = null;
            message.Dispose();
            ReturnArgsToCache(context, requestArgs);
        }

        static void ReturnArgsToCache(SessionContext<TProxy> context, InvokeMethodCoreArgs args)
        {
            //args.Message.Arguments = Memory<byte>.Empty;
            args.Message.Dispose();
            InvokeMethodCoreArgs.Return(args);
        }
    }

    private class InvokeMethodCoreArgs
    {

        private static readonly ConcurrentBag<InvokeMethodCoreArgs> _cache = new();
        public InvocationMessage Message = null!;
        public SessionContext<TProxy> SessionContext = null!;
        public InvokeMethodCoreDelegate InvokeMethodCore = null!;

        public static InvokeMethodCoreArgs Get() =>
            _cache.TryTake(out var result) ? result : new InvokeMethodCoreArgs();

        public static void Return(InvokeMethodCoreArgs args)
        {
            args.Message = null!;
            args.SessionContext = null!;
            _cache.Add(args);
        }
    }
    
    INexusList<T> ICollectionStore.GetList<T>(ushort id)
    {
        // The hot path should be to 
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (SessionContext.Session == null && SessionContext is ConfigurerSessionContext<TProxy> configurer)
            return configurer.CollectionManager.GetList<T>(id);
        
        return SessionContext.Session!.CollectionManager.GetList<T>(id);
    }

    ValueTask ICollectionStore.StartCollection(ushort id, INexusDuplexPipe pipe)
    {
        return SessionContext.Session.CollectionManager.StartServerCollectionConnection(id, pipe, SessionContext.Session);
    }
}
