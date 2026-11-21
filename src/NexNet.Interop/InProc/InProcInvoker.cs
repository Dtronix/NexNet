using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using NexNet.Collections.Lists;
using NexNet.Interop.Reflection;
using NexNet.Pipes;

namespace NexNet.Interop.InProc;

/// <summary>
/// Runs remote-proxy calls on a C# client's real server proxy: the typed-JSON arguments are decoded to the parameter
/// types of the server nexus interface, the method is invoked and its result encoded back, so the in-process row goes
/// through the same typed-JSON path as the language hosts.
/// </summary>
internal sealed class InProcInvoker : IRemoteInvoker
{
    private static readonly MethodInfo AwaitResultMethod =
        typeof(InProcInvoker).GetMethod(nameof(AwaitResultAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetChannelMethod =
        typeof(NexusDuplexPipeExtensions).GetMethod(nameof(NexusDuplexPipeExtensions.GetChannel))!;

    private static readonly ConcurrentDictionary<Type, Func<object, Task<object?>>> ResultAwaiters = new();

    private readonly Func<object> _proxy;
    private readonly ConcurrentDictionary<Task, byte> _detached = new();
    private int _pending;

    /// <summary>Counts the collection watches of one client that are still attached.</summary>
    internal sealed class WatchCounter
    {
        private int _count;

        /// <summary>Watches not disposed.</summary>
        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Decrement() => Interlocked.Decrement(ref _count);
    }

    /// <param name="proxy">Returns the client's current server proxy.</param>
    public InProcInvoker(Func<object> proxy)
    {
        _proxy = proxy;
    }

    /// <summary>Invocations started and not completed.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Collection watches (<see cref="IRemoteList{T}.WatchAsync"/>) not disposed.</summary>
    public WatchCounter Watches { get; } = new();

    /// <summary>Fire-and-forget invocations not completed.</summary>
    public Task WhenDetachedCompleteAsync() => Task.WhenAll(_detached.Keys);

    public void Invoke(RemoteInvocation invocation)
    {
        var task = CallAsync(invocation);
        if (task.IsCompleted)
        {
            // Errors of fire-and-forget calls are not observed.
            _ = task.Exception;
            return;
        }

        _detached.TryAdd(task, 0);
        task.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception;
                ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _);
            },
            _detached,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async ValueTask InvokeAsync(RemoteInvocation invocation) => await CallAsync(invocation).ConfigureAwait(false);

    public async ValueTask<JsonNode?> InvokeWithResultAsync(RemoteInvocation invocation)
    {
        var method = NexusTypes.FindMethod(_proxy().GetType(), invocation.Method);
        var result = await CallAsync(invocation).ConfigureAwait(false);
        return ClrCodec.Encode(method.ReturnType.GetGenericArguments()[0], result);
    }

    public IRemoteList<T> GetList<T>(RemoteCollection collection, TypedCodec<T> codec)
    {
        var proxy = _proxy();
        var list = (INexusList<T>)NexusTypes.FindProperty(proxy.GetType(), collection).GetValue(proxy)!;
        return new InProcRemoteList<T>(collection, list, Watches);
    }

    /// <summary>The token of <paramref name="cancel"/>; disposing the result stops an <see cref="RemoteCancelKind.After"/> timer.</summary>
    internal static IDisposable? CreateCancellation(RemoteCancel cancel, out CancellationToken token)
    {
        switch (cancel.Kind)
        {
            case RemoteCancelKind.After:
                var source = new CancellationTokenSource(cancel.AfterMilliseconds);
                token = source.Token;
                return source;
            case RemoteCancelKind.Token:
                token = cancel.Token;
                return null;
            default:
                token = CancellationToken.None;
                return null;
        }
    }

    private async Task<object?> CallAsync(RemoteInvocation invocation)
    {
        Interlocked.Increment(ref _pending);
        var timer = CreateCancellation(invocation.Cancel, out var token);
        try
        {
            var proxy = _proxy();
            var method = NexusTypes.FindMethod(proxy.GetType(), invocation.Method);
            var arguments = BuildArguments(method, invocation, token);

            object? returned;
            try
            {
                returned = method.Invoke(proxy, arguments);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }

            switch (returned)
            {
                case null:
                    return null;
                case ValueTask valueTask:
                    await valueTask.ConfigureAwait(false);
                    return null;
                default:
                    var awaiter = ResultAwaiters.GetOrAdd(returned.GetType(), static t =>
                        AwaitResultMethod.MakeGenericMethod(t.GetGenericArguments()[0])
                            .CreateDelegate<Func<object, Task<object?>>>());
                    return await awaiter(returned).ConfigureAwait(false);
            }
        }
        finally
        {
            timer?.Dispose();
            Interlocked.Decrement(ref _pending);
        }
    }

    private static object?[] BuildArguments(MethodInfo method, RemoteInvocation invocation, CancellationToken token)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        var next = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(CancellationToken))
            {
                arguments[i] = token;
            }
            else if (type == typeof(INexusDuplexPipe))
            {
                arguments[i] = RequirePipe(invocation).Pipe;
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(INexusDuplexChannel<>))
            {
                arguments[i] = GetChannelMethod.MakeGenericMethod(type.GetGenericArguments()[0])
                    .Invoke(null, [RequirePipe(invocation).Pipe]);
            }
            else
            {
                if (next >= invocation.Arguments.Count)
                    throw new ArgumentException($"{invocation.Method} takes more arguments than the {invocation.Arguments.Count} given.");
                arguments[i] = ClrCodec.Decode(type, invocation.Arguments[next++]);
            }
        }

        if (next != invocation.Arguments.Count)
            throw new ArgumentException($"{invocation.Method} takes {next} serialized arguments, not {invocation.Arguments.Count}.");
        return arguments;
    }

    private static InProcRemotePipe RequirePipe(RemoteInvocation invocation)
        => invocation.Pipe as InProcRemotePipe
           ?? throw new ArgumentException($"{invocation.Method} needs a pipe created by this client's CreatePipeAsync.");

    private static async Task<object?> AwaitResultAsync<T>(object valueTask) => await ((ValueTask<T>)valueTask).ConfigureAwait(false);
}
