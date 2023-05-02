using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NexNet.Invocation;

public abstract class ServerHubBase<TProxy> : HubBase<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    public ServerSessionContext<TProxy> Context => Unsafe.As<ServerSessionContext<TProxy>>(SessionContext)!;

    internal ValueTask<IIdentity?> Authenticate(byte[]? authenticationToken)
    {
        return OnAuthenticate(authenticationToken);
    }

    /// <summary>
    /// Called on server sessions and called with the client's Authentication token.
    /// </summary>
    protected virtual ValueTask<IIdentity?> OnAuthenticate(byte[]? authenticationToken)
    {
        return ValueTask.FromResult((IIdentity?)null);
    }
}
