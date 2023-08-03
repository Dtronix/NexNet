using System.Collections.Generic;
using NexNet.Cache;
using NexNet.Internals.Pipes;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet.Internals;

/// <summary>
/// Base interface for all sessions.
/// </summary>
internal interface INexusSession : ISessionMessenger
{
    /// <summary>
    /// ID of this session.
    /// </summary>
    long Id { get; }

    /// <summary>
    /// List all the groups that this session is registered with.  Always an empty list on client sessions.
    /// </summary>
    List<int> RegisteredGroups { get; }

    /// <summary>
    /// Manages all the sessions connection on the server. Null if this is not a server session.
    /// </summary>
    SessionManager? SessionManager { get; }

    /// <summary>
    ///  Store for maintaining data between invocations on the nexus.
    /// </summary>
    public SessionStore SessionStore { get; }

    /// <summary>
    /// Manages all the invocations for this session.
    /// </summary>
    SessionInvocationStateManager SessionInvocationStateManager { get; }

    /// <summary>
    /// Last tick that this session received a message.
    /// </summary>
    long LastReceived { get; }

    INexusLogger? Logger { get; }

    /// <summary>
    /// Contains 
    /// </summary>
    internal CacheManager CacheManager { get; }

    /// <summary>
    /// Configurations for the session.
    /// </summary>
    ConfigBase Config { get; }

    ConnectionState State { get; }
    bool IsServer { get; }
    NexusPipeManager PipeManager { get; }

    /// <summary>
    /// Disconnects the session if the last received time is less than the timeout ticks.
    /// </summary>
    /// <param name="timeoutTicks">Normally use Environment.TickCount64 - Timeout</param>
    /// <returns>True upon successful disconnection due to timeout.  False otherwise.</returns>
    bool DisconnectIfTimeout(long timeoutTicks);
}

/// <summary>
/// Base session with proxy information.
/// </summary>
/// <typeparam name="TProxy">Proxy type for the session.</typeparam>
internal interface INexusSession<TProxy> : INexusSession
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Contains 
    /// </summary>
    internal new SessionCacheManager<TProxy> CacheManager { get; }
}

