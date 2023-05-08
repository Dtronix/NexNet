﻿namespace NexNet.Invocation;

/// <summary>
/// Interface for selection of clients to invoke methods on.
/// </summary>
/// <typeparam name="TProxy">Proxy class used for invocation.</typeparam>
public interface IProxyClients<out TProxy>
    where TProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Proxy for the session who invoke this method.
    /// </summary>
    TProxy Caller { get; }

    /// <summary>
    /// Proxy for all connected sessions.
    /// </summary>
    TProxy All { get; }

    /// <summary>
    /// Proxy for all connected sessions except the current one.
    /// </summary>
    TProxy Others { get; }

    /// <summary>
    /// Proxy for a specific client selected by session id.
    /// </summary>
    /// <param name="id">Session id to get a proxy for.</param>
    /// <returns>Proxy</returns>
    TProxy Client(long id);

    /// <summary>
    /// Proxy for a specific clients selected by session ids.
    /// </summary>
    /// <param name="ids">Session ids to get a proxies for.</param>
    /// <returns>Proxy</returns>
    TProxy Clients(long[] ids);

    /// <summary>
    /// Proxy for all clients part of the specified group.
    /// </summary>
    /// <param name="groupName">Group name to get the proxy for.</param>
    /// <returns>Proxy</returns>
    TProxy Group(string groupName);

    /// <summary>
    /// Proxy for all clients part of the specified groups.
    /// </summary>
    /// <param name="groupName">Group names to get the proxies for.</param>
    /// <returns>Proxy</returns>
    TProxy Groups(string[] groupName);
}
