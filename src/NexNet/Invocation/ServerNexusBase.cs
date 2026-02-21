using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Messages;

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

    /// <summary>
    /// Called by the generated auth guard before an authorized method is invoked.
    /// Override to implement custom authorization logic.
    /// </summary>
    /// <param name="context">The server session context.</param>
    /// <param name="methodId">The method ID being invoked.</param>
    /// <param name="methodName">The name of the method being invoked.</param>
    /// <param name="requiredPermissions">The required permission values (as ints) from the attribute. Empty if marker-only.</param>
    /// <returns>The authorization result controlling invocation behavior.</returns>
    protected virtual ValueTask<AuthorizeResult> OnAuthorize(
        ServerSessionContext<TProxy> context,
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions)
    {
        return new ValueTask<AuthorizeResult>(AuthorizeResult.Allowed);
    }

    /// <summary>
    /// Wrapper called by generated auth guard code to invoke OnAuthorize with the typed context.
    /// </summary>
    protected async ValueTask<AuthorizeResult> Authorize(
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions)
    {
        try
        {
            return await OnAuthorize(Context, methodId, methodName, requiredPermissions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail-safe: if OnAuthorize itself throws, treat as disconnect.
            SessionContext.Session.Logger?.LogError(ex, $"OnAuthorize threw an exception for method '{methodName}' (id={methodId}). Disconnecting session.");
            return AuthorizeResult.Disconnect;
        }
    }

    /// <summary>
    /// Sends an unauthorized invocation result back to the caller.
    /// Called by generated auth guard code.
    /// </summary>
    protected async ValueTask SendUnauthorizedResult(ushort invocationId)
    {
        var message = SessionContext.PoolManager.Rent<InvocationResultMessage>();
        message.InvocationId = invocationId;
        message.Result = null;
        message.State = InvocationResultMessage.StateType.Unauthorized;
        await SessionContext.Session.SendMessage(message).ConfigureAwait(false);
        message.Dispose();
    }
}
