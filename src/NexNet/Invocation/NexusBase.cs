using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Serialization;
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

    // Invocations that expect a result, by invocation ID, from receipt until they complete. A cancellation that
    // arrives before the method has registered its token (queued for a slot, or just started) cancels it when it
    // does. A Dictionary under a lock rather than a ConcurrentDictionary: it reuses its entries, so tracking an
    // invocation allocates nothing.
    private readonly Dictionary<int, InvocationCancellation> _resultInvocations = new();
    private readonly Lock _resultInvocationsLock = new();

    private struct InvocationCancellation
    {
        // The invocation's own source, while its method runs and nothing has canceled it.
        public CancellationTokenSource? Source;

        // The caller canceled the invocation.
        public bool Requested;
    }

    // Shared by the session's fire-and-forget invocations; canceled when the session ends. Created on first use.
    private CancellationTokenSource? _sessionCancellation;

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
        args.Nexus = this;
        return InvokeMethodCoreTask(args);
    }

    CancellationTokenSource IMethodInvoker.RegisterCancellationToken(IInvocationMessage message)
    {
        if ((message.Flags & InvocationFlags.IgnoreReturn) != 0)
        {
            var shared = Volatile.Read(ref _sessionCancellation);
            if (shared != null)
                return shared;

            var created = new CancellationTokenSource();
            return Interlocked.CompareExchange(ref _sessionCancellation, created, null) ?? created;
        }

        var cts = SessionContext.PoolManager.CancellationTokenSourcePool.Rent();
        bool requested;
        lock (_resultInvocationsLock)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_resultInvocations, message.InvocationId, out _);
            requested = entry.Requested;

            // Canceled before the method started: the source is canceled below and not kept for the pool.
            if (!requested)
                entry.Source = cts;
        }

        if (requested)
            cts.Cancel();

        return cts;
    }

    void IMethodInvoker.ReturnCancellationToken(IInvocationMessage message, CancellationTokenSource cancellationTokenSource)
    {
        // The shared source lives as long as the session.
        if ((message.Flags & InvocationFlags.IgnoreReturn) != 0)
            return;

        // Taken back only if it is this invocation's own source and no cancellation is using it.
        lock (_resultInvocationsLock)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_resultInvocations, message.InvocationId);
            if (Unsafe.IsNullRef(ref entry) || entry.Source != cancellationTokenSource)
                return;

            entry.Source = null;
        }

        // Try to reset the cts for another operation.
        SessionContext.PoolManager.CancellationTokenSourcePool.Return(cancellationTokenSource);
    }

    async ValueTask<INexusDuplexPipe> IMethodInvoker.RegisterDuplexPipe(byte startId, IInvocationMessage message)
    {
        var pipe = await SessionContext.Session.PipeManager.RegisterPipe(startId).ConfigureAwait(false);

        // The pipe now counts against the pipe limit; free the execution slot for other invocations.
        (message as InvocationMessage)?.Slot?.Release();
        return pipe;
    }

    NexusSerializerOptions IMethodInvoker.SerializerOptions =>
        SessionContext.Session?.Config.SerializerOptions ?? NexusSerializerOptions.Untrusted;

    ValueTask IMethodInvoker.ReturnDuplexPipe(INexusDuplexPipe pipe)
    {
        return SessionContext.Session.PipeManager.DeregisterPipe(pipe);
    }

    void IMethodInvoker.TrackInvocation(InvocationMessage message)
    {
        if ((message.Flags & InvocationFlags.IgnoreReturn) != 0)
            return;

        lock (_resultInvocationsLock)
        {
            _resultInvocations.TryAdd(message.InvocationId, default);
        }
    }

    void IMethodInvoker.CancelInvocation(InvocationCancellationMessage message)
    {
        CancellationTokenSource? cts;
        lock (_resultInvocationsLock)
        {
            // Not tracked: completed already, or never received.
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_resultInvocations, message.InvocationId);
            if (Unsafe.IsNullRef(ref entry))
                return;

            entry.Requested = true;
            cts = entry.Source;
            entry.Source = null;
        }

        cts?.Cancel();
    }

    /// <summary>
    /// Stops tracking a completed invocation that expected a result.
    /// </summary>
    private void UntrackInvocation(InvocationMessage message)
    {
        lock (_resultInvocationsLock)
        {
            _resultInvocations.Remove(message.InvocationId);
        }
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

        lock (_resultInvocationsLock)
        {
            foreach (var invocation in _resultInvocations.Values)
                invocation.Source?.Dispose();

            _resultInvocations.Clear();
        }

        // Not disposed: running fire-and-forget invocations may still observe the token.
        Interlocked.Exchange(ref _sessionCancellation, null)?.Cancel();
    }

    // Pooled: awaited exactly once, by the session's invocation work item.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask InvokeMethodCoreTask(InvokeMethodCoreArgs requestArgs)
    {
        var context = requestArgs.SessionContext;
        //LocalContext.Value = context;

        // If we have the flag to ignore the return value, then all we have to do is invoke and then
        // return the rest args to the cache.
        if ((requestArgs.Message.Flags & InvocationFlags.IgnoreReturn) != 0)
        {
            try
            {
                await requestArgs.InvokeMethodCore(requestArgs.Message, null).ConfigureAwait(false);
            }
            catch (Exception e) when (e is NexusMessageTooLargeException or NexusBusyException)
            {
                // A limit was reached (e.g. a pipe was refused). Not a fault in the method.
                context.Session.Logger?.LogWarning($"Method {requestArgs.Message.MethodId} failed: {e.Message}");
            }
            finally
            {
                ReturnArgsToCache(context, requestArgs);
            }

            return;
        }

        var message = context.PoolManager.Rent<InvocationResultMessage>();
        message.InvocationId = requestArgs.Message.InvocationId;

        var bufferWriterCache = requestArgs.SessionContext.PoolManager.BufferWriterPool;
        if (!bufferWriterCache.TryTake(out var bufferWriter))
            bufferWriter = BufferWriter<byte>.Create();

        try
        {
            await requestArgs.InvokeMethodCore(requestArgs.Message, bufferWriter).ConfigureAwait(false);

            // Refused by authorization: the method did not run. A disconnecting session gets no result.
            if (requestArgs.Message.AuthorizeResult != AuthorizeResult.Allowed)
            {
                bufferWriterCache.Add(bufferWriter);
                if (requestArgs.Message.AuthorizeResult == AuthorizeResult.Unauthorized)
                {
                    message.Result = null;
                    message.State = InvocationResultMessage.StateType.Unauthorized;
                    await context.Session.SendMessage(message).ConfigureAwait(false);
                }

                return;
            }

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
            try
            {
                await context.Session.SendMessage(message).ConfigureAwait(false);
            }
            finally
            {
                if (bufferResult != null)
                    bufferWriter.Deallocate(bufferResult.Value);
            }

            bufferWriterCache.Add(bufferWriter);
        }
        catch (OperationCanceledException)
        {
            // Every invocation that expects a result gets one: the caller keeps its id reserved until then.
            context.Session.Logger?.LogTrace("Invocation canceled.");
            message.Result = null;
            message.State = InvocationResultMessage.StateType.Canceled;
            await context.Session.SendMessage(message).ConfigureAwait(false);
        }
        catch (Exception e) when (e is NexusMessageTooLargeException or NexusBusyException)
        {
            // A limit was reached (the result is too large, or a pipe was refused); fail the call. Not a fault in
            // the method, so not logged as an error.
            context.Session.Logger?.LogWarning($"Method {requestArgs.Message.MethodId} failed: {e.Message}");
            bufferWriterCache.Add(bufferWriter);
            message.Result = null;
            message.State = InvocationResultMessage.StateType.Exception;
            await context.Session.SendMessage(message).ConfigureAwait(false);
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
            requestArgs.Nexus.UntrackInvocation(requestArgs.Message);
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
        public NexusBase<TProxy> Nexus = null!;

        public static InvokeMethodCoreArgs Get() =>
            _cache.TryTake(out var result) ? result : new InvokeMethodCoreArgs();

        public static void Return(InvokeMethodCoreArgs args)
        {
            args.Message = null!;
            args.SessionContext = null!;
            args.Nexus = null!;
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
