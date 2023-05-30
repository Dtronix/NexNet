namespace NexNet.Invocation;

/// <summary>
/// Interface for selection of clients to invoke methods on.
/// </summary>
/// <typeparam name="TProxy">Proxy class used for invocation.</typeparam>
public interface IProxyClients<out TProxy> : IProxyBase<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Proxy for the session who invoke this method.
    /// </summary>
    TProxy Caller { get; }

    /// <summary>
    /// Proxy for all connected sessions except the current one.
    /// </summary>
    TProxy Others { get; }
}
