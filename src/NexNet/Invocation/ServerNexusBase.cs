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
    /// Called by generated auth guard code. Runs the authorization check and handles
    /// Unauthorized (sends error result) and Disconnect (disconnects session) outcomes.
    /// Returns true if the method invocation should proceed, false otherwise.
    /// </summary>
    /// <param name="methodId">The method ID being invoked.</param>
    /// <param name="methodName">The name of the method being invoked.</param>
    /// <param name="requiredPermissions">The required permission int values from the attribute.</param>
    /// <param name="invocationId">The invocation ID for sending result messages.</param>
    /// <param name="hasReturnChannel">Whether the caller expects a return value (returnBuffer != null).</param>
    /// <returns>True if authorized and the method should proceed; false if handled (unauthorized or disconnected).</returns>
    protected async ValueTask<bool> CheckAuthorization(
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions,
        ushort invocationId,
        bool hasReturnChannel)
    {
        AuthorizeResult result;
        try
        {
            result = await OnAuthorize(Context, methodId, methodName, requiredPermissions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SessionContext.Session.Logger?.LogError(ex, $"OnAuthorize threw an exception for method '{methodName}' (id={methodId}). Disconnecting session.");
            result = AuthorizeResult.Disconnect;
        }

        switch (result)
        {
            case AuthorizeResult.Allowed:
                return true;

            case AuthorizeResult.Disconnect:
                await SessionContext.Session.DisconnectAsync(DisconnectReason.Unauthorized).ConfigureAwait(false);
                return false;

            case AuthorizeResult.Unauthorized:
                if (hasReturnChannel)
                {
                    var message = SessionContext.PoolManager.Rent<InvocationResultMessage>();
                    message.InvocationId = invocationId;
                    message.Result = null;
                    message.State = InvocationResultMessage.StateType.Unauthorized;
                    await SessionContext.Session.SendMessage(message).ConfigureAwait(false);
                    message.Dispose();
                }
                return false;

            default:
                return false;
        }
    }
}
