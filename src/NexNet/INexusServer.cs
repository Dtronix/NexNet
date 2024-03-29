﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet;

/// <summary>
/// Interface for a NexNet server.
/// </summary>
public interface INexusServer : IAsyncDisposable
{
    /// <summary>
    /// True if the server is running, false otherwise.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Configurations the server us currently using.
    /// </summary>
    ServerConfig Config { get; }

    /// <summary>
    /// Task which completes upon the server stopping.
    /// </summary>
    Task? StoppedTask { get; }

    /// <summary>
    /// Starts the server.
    /// </summary>
    /// <exception cref="InvalidOperationException">Throws when the server is already running.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the server.
    /// </summary>
    Task StopAsync();
}

internal interface INexusServer<TClientProxy> : INexusServer
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    ConcurrentBag<ServerNexusContext<TClientProxy>> ServerNexusContextCache { get; }
}
