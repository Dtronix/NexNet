using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace NexNet.Interop;

/// <summary>One call of a generated remote proxy.</summary>
public sealed class RemoteInvocation
{
    /// <summary>Creates an invocation.</summary>
    /// <param name="method">The invoked method.</param>
    /// <param name="arguments">The serialized arguments in wire order, as typed JSON.</param>
    /// <param name="cancel">The cancellation of the call.</param>
    /// <param name="pipe">The pipe passed for a pipe or channel parameter, or null.</param>
    public RemoteInvocation(RemoteMethod method, JsonArray arguments, RemoteCancel cancel, IRemotePipe? pipe)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        Cancel = cancel ?? RemoteCancel.None;
        Pipe = pipe;
    }

    /// <summary>The invoked method.</summary>
    public RemoteMethod Method { get; }

    /// <summary>The serialized arguments in wire order (<see cref="RemoteMethod.ParameterNames"/>), as typed JSON.</summary>
    public JsonArray Arguments { get; }

    /// <summary>The cancellation of the call.</summary>
    public RemoteCancel Cancel { get; }

    /// <summary>The pipe passed for a pipe or channel parameter, or null.</summary>
    public IRemotePipe? Pipe { get; }
}

/// <summary>
/// Runs the calls of a generated remote proxy on a client under test. Implemented by the harness drivers (H04 in-proc,
/// H05 remote); generated code only calls it.
/// </summary>
public interface IRemoteInvoker
{
    /// <summary>A fire-and-forget (<c>void</c>) method. Returns once the call is queued; errors are not observed.</summary>
    void Invoke(RemoteInvocation invocation);

    /// <summary>A <c>ValueTask</c> method. Completes when the remote call does; rethrows its error.</summary>
    ValueTask InvokeAsync(RemoteInvocation invocation);

    /// <summary>A <c>ValueTask&lt;T&gt;</c> method. Returns the result as typed JSON; rethrows the call's error.</summary>
    ValueTask<JsonNode?> InvokeWithResultAsync(RemoteInvocation invocation);

    /// <summary>The client-side view of a synchronized collection.</summary>
    IRemoteList<T> GetList<T>(RemoteCollection collection, TypedCodec<T> codec);
}

/// <summary>A generated remote proxy.</summary>
public interface IRemoteProxy
{
    /// <summary>The invoker every call goes through.</summary>
    IRemoteInvoker Invoker { get; }
}

/// <summary>A generated remote proxy that a driver can create from an invoker.</summary>
/// <typeparam name="TSelf">The generated proxy type.</typeparam>
public interface IRemoteProxy<TSelf> : IRemoteProxy
    where TSelf : IRemoteProxy<TSelf>
{
    /// <summary>Creates a proxy that forwards every call to <paramref name="invoker"/>.</summary>
    static abstract TSelf Create(IRemoteInvoker invoker);
}
