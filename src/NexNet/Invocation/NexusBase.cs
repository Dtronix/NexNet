using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.Invocation;

/// <summary>
/// Base hub with common methods used between server and client.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
public abstract class NexusBase<TProxy> : IMethodInvoker<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _cancellableInvocations = new();
    internal readonly ConcurrentDictionary<int, NexusPipe> InvocationPipes = new();

    internal SessionContext<TProxy> SessionContext { get; set; } = null!;

    // ReSharper disable once StaticMemberInGenericType
    private static readonly ConcurrentBag<BufferWriter<byte>> _bufferWriters = new ConcurrentBag<BufferWriter<byte>>();

    ValueTask IMethodInvoker<TProxy>.InvokeMethod(InvocationMessage message)
    {
        var args = InvokeMethodCoreArgs.Get();
        args.Message = message;
        args.SessionContext = SessionContext;
        args.InvokeMethodCore = InvokeMethodCore;
        return InvokeMethodCoreTask(args);
    }

    CancellationTokenSource IMethodInvoker<TProxy>.RegisterCancellationToken(int invocationId)
    {
        var cts = SessionContext.CacheManager.CancellationTokenSourceCache.Rent();

        _cancellableInvocations.TryAdd(invocationId, cts);
        return cts;
    }

    void IMethodInvoker<TProxy>.ReturnCancellationToken(int invocationId)
    {
        if (!_cancellableInvocations.TryRemove(invocationId, out var cts))
            return;

        // Try to reset the cts for another operation.
        SessionContext.CacheManager.CancellationTokenSourceCache.Return(cts);
    }

    async ValueTask<NexusPipe> IMethodInvoker<TProxy>.RegisterPipeReader(int invocationId, CancellationToken? cancellationToken)
    {
        var pipe = SessionContext.CacheManager.NexusPipeCache.Rent(SessionContext.Session, invocationId);
        if (!InvocationPipes.TryAdd(invocationId, pipe))
        {
            throw new InvalidOperationException(
                "Tried to create a pipe for a invocation which already has an active pipe.");
        }

        if(cancellationToken != null)
            pipe.RegisterCancellationToken(cancellationToken.Value);

        // Send the ready message.
        var message = SessionContext.CacheManager.Rent<PipeReadyMessage>();
        message.InvocationId = invocationId;
        await SessionContext.Session.SendMessage(message);
        SessionContext.CacheManager.Return(message);

        return pipe;
    }

    async ValueTask IMethodInvoker<TProxy>.ReturnPipeReader(int invocationId)
    {
        if (InvocationPipes.TryRemove(invocationId, out var pipe))
        {
            await pipe.ReaderCompleted();
            SessionContext.CacheManager.NexusPipeCache.Return(pipe!);
        }
    }


    void IMethodInvoker<TProxy>.CancelInvocation(InvocationCancellationMessage message)
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

        // Close any open pipes.
        foreach (var pipe in InvocationPipes)
            pipe.Value.UpstreamComplete();

        InvocationPipes.Clear();
    }

    private static async ValueTask InvokeMethodCoreTask(InvokeMethodCoreArgs requestArgs)
    {
        var context = requestArgs.SessionContext;
        //LocalContext.Value = context;

        // If we have the flag to ignore the return value, then all we have to do is invoke and then
        // return the rest args to the cache.
        if (requestArgs.Message.Flags == InvocationFlags.IgnoreReturn)
        {
            await requestArgs.InvokeMethodCore(requestArgs.Message, null).ConfigureAwait(false);
            ReturnArgsToCache(context, requestArgs);
            return;
        }

        var message = context.CacheManager.Rent<InvocationResultMessage>();
        message.InvocationId = requestArgs.Message.InvocationId;

        try
        {
            if (!_bufferWriters.TryTake(out var bufferWriter))
                bufferWriter = BufferWriter<byte>.Create();

            await requestArgs.InvokeMethodCore(requestArgs.Message, bufferWriter).ConfigureAwait(false);
            Owned<ReadOnlySequence<byte>>? bufferResult = null;
            // If the length is zero, there is no return value.
            if (bufferWriter.Length == 0)
            {
                message.Result = null;
            }
            else
            {
                bufferResult = bufferWriter.Flush();
                message.Result = bufferResult.Value;
            }

            message.State = InvocationResultMessage.StateType.CompletedResult;
            await context.Session.SendMessage(message).ConfigureAwait(false);
            bufferResult?.Dispose();
            _bufferWriters.Add(bufferWriter);
        }
        catch (TaskCanceledException)
        {
            // noop
        }
        catch (Exception)
        {
            message.Result = null;
            message.State = InvocationResultMessage.StateType.Exception;
            await context.Session.SendMessage(message).ConfigureAwait(false);
        }
        finally
        {
            message.Result = null;
            context.CacheManager.Return(message);
            ReturnArgsToCache(context, requestArgs);
        }

        static void ReturnArgsToCache(SessionContext<TProxy> context, InvokeMethodCoreArgs args)
        {
            args.Message.Arguments = Memory<byte>.Empty;
            context.CacheManager.Return(args.Message);
            InvokeMethodCoreArgs.Return(args);
        }
    }

    private class InvokeMethodCoreArgs
    {
        private static readonly ConcurrentBag<InvokeMethodCoreArgs> _cache = new();
        public InvocationMessage Message = null!;
        public SessionContext<TProxy> SessionContext = null!;
        public Func<InvocationMessage, IBufferWriter<byte>?, ValueTask> InvokeMethodCore = null!;

        public static InvokeMethodCoreArgs Get() =>
            _cache.TryTake(out var result) ? result : new InvokeMethodCoreArgs();

        public static void Return(InvokeMethodCoreArgs args)
        {
            args.Message = null!;
            args.SessionContext = null!;
            _cache.Add(args);
        }

        //public static void Clear() => _cache.Clear();
    }
}
