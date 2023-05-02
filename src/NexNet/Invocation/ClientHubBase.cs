using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NexNet.Invocation;

public abstract class ClientHubBase<TProxy> : HubBase<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    public ClientSessionContext<TProxy> Context => Unsafe.As<ClientSessionContext<TProxy>>(SessionContext)!;

    internal ValueTask Reconnected()
    {
        return OnReconnected();
    }
    protected virtual ValueTask OnReconnected()
    {
        return ValueTask.CompletedTask;
    }

    internal ValueTask Reconnecting()
    {
        return OnReconnecting();
    }

    protected virtual ValueTask OnReconnecting()
    {
        return ValueTask.CompletedTask;
    }
}
