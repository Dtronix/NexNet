using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace NexNet.Invocation;

/// <summary>
/// Base class used for all server session hubs.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
public abstract class ServerNexusBase<TProxy> : NexusBase<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Context for this current session.
    /// </summary>
    public ServerSessionContext<TProxy> Context => Unsafe.As<ServerSessionContext<TProxy>>(SessionContext)!;

    internal ValueTask<IIdentity?> Authenticate(ReadOnlyMemory<byte>? authenticationToken)
    {
        return OnAuthenticate(authenticationToken);
    }

    /// <summary>
    /// Called on server sessions and called with the client's Authentication token.
    /// </summary>
    protected virtual ValueTask<IIdentity?> OnAuthenticate(ReadOnlyMemory<byte>? authenticationToken)
    {
        return ValueTask.FromResult((IIdentity?)null);
    }
}
