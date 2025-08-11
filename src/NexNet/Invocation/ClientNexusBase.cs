using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NexNet.Invocation;

/// <summary>
/// Base class used for all client nexuses.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
public abstract class ClientNexusBase<TProxy> : NexusBase<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Context for this current session.
    /// </summary>
    public ClientSessionContext<TProxy> Context => Unsafe.As<ClientSessionContext<TProxy>>(SessionContext);

    /// <summary>
    /// Invoke when the connection to the server has been lost and it in the process of reconnecting.
    /// </summary>
    /// <returns></returns>
    internal Task Reconnecting()
    {
        return OnReconnecting();
    }

    /// <summary>
    /// Invoked when the connection to the server has been lost and it in the process of reconnecting.
    /// </summary>
    /// <returns></returns>
    protected virtual Task OnReconnecting()
    {
        return Task.CompletedTask;
    }
}
