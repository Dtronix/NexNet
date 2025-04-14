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
    
    internal ValueTask NexusInitialize()
    {
        return OnNexusInitialize();
    }
    
    /// <summary>
    /// Initializes the nexus. Is performed after the session has invoked <see cref="OnAuthenticate"/> (if applicable), but prior to <see cref="NexusBase{TProxy}.OnConnected"/>.
    /// Good for registration of groups.  If an exception occurs on this method, the session will be disconnected.  Invoked on the same task as <see cref="OnAuthenticate"/>.
    /// </summary>
    /// <returns>Task returns upon completion of the initialization.</returns>
    protected virtual ValueTask OnNexusInitialize()
    {
        return ValueTask.CompletedTask;
    }
}
