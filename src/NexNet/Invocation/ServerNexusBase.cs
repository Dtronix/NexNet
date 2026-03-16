using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Transports;

namespace NexNet.Invocation;

/// <summary>
/// Base class used for all server session hubs.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
public abstract class ServerNexusBase<TProxy> : NexusBase<TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    private Dictionary<int, (AuthorizeResult Result, long ExpiresAtTicks)>? _authCache;
    internal Func<long>? TickCountOverride;

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
    /// Invalidates all cached authorization results for this session.
    /// </summary>
    protected void InvalidateAuthorizationCache()
    {
        _authCache?.Clear();
    }

    /// <summary>
    /// Invalidates the cached authorization result for a specific method in this session.
    /// </summary>
    /// <param name="methodId">The method ID whose cached result should be removed.</param>
    protected void InvalidateAuthorizationCache(int methodId)
    {
        _authCache?.Remove(methodId);
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
    /// <param name="cacheDurationSeconds">Per-method cache override: -1 = use server config, 0 = no cache, &gt;0 = seconds.</param>
    /// <returns>True if authorized and the method should proceed; false if handled (unauthorized or disconnected).</returns>
    protected async ValueTask<bool> CheckAuthorization(
        int methodId,
        string methodName,
        ReadOnlyMemory<int> requiredPermissions,
        ushort invocationId,
        bool hasReturnChannel,
        int cacheDurationSeconds)
    {
        // Resolve effective cache duration in milliseconds
        long cacheDurationMs = 0;
        if (cacheDurationSeconds > 0)
        {
            // Method/collection attribute override
            cacheDurationMs = cacheDurationSeconds * 1000L;
        }
        else if (cacheDurationSeconds == -1)
        {
            // Fall back to server config
            if (SessionContext.Session.Config is ServerConfig serverConfig
                && serverConfig.AuthorizationCacheDuration is { } configDuration
                && configDuration > TimeSpan.Zero)
            {
                cacheDurationMs = (long)configDuration.TotalMilliseconds;
            }
        }
        // cacheDurationSeconds == 0 means explicitly no cache

        // Check cache
        if (cacheDurationMs > 0 && _authCache != null
            && _authCache.TryGetValue(methodId, out var cached))
        {
            if ((TickCountOverride?.Invoke() ?? Environment.TickCount64) < cached.ExpiresAtTicks)
            {
                // Cache hit
                return await HandleAuthResult(cached.Result, invocationId, hasReturnChannel)
                    .ConfigureAwait(false);
            }

            // Expired
            _authCache.Remove(methodId);
        }

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

        // Cache Allowed and Unauthorized results (never Disconnect or exceptions)
        if (cacheDurationMs > 0 && result is AuthorizeResult.Allowed or AuthorizeResult.Unauthorized)
        {
            _authCache ??= new Dictionary<int, (AuthorizeResult, long)>();
            _authCache[methodId] = (result, (TickCountOverride?.Invoke() ?? Environment.TickCount64) + cacheDurationMs);
        }

        return await HandleAuthResult(result, invocationId, hasReturnChannel).ConfigureAwait(false);
    }

    private async ValueTask<bool> HandleAuthResult(
        AuthorizeResult result,
        ushort invocationId,
        bool hasReturnChannel)
    {
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
                    try
                    {
                        message.InvocationId = invocationId;
                        message.Result = null;
                        message.State = InvocationResultMessage.StateType.Unauthorized;
                        await SessionContext.Session.SendMessage(message).ConfigureAwait(false);
                    }
                    finally
                    {
                        message.Dispose();
                    }
                }
                return false;

            default:
                return false;
        }
    }
}
