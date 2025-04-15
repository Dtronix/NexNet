using System;
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
    /// Configurations the server us currently using.
    /// </summary>
    ServerConfig Config { get; }

    /// <summary>
    /// Task which completes upon the server stopping.
    /// </summary>
    Task? StoppedTask { get; }

    /// <summary>
    /// State of the server.
    /// </summary>
    NexusServerState State { get; }

    /// <summary>
    /// True if the server has been Configured and ready to start.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Starts the server.  Returns upon completion of the start process.  Does not block.
    /// </summary>
    /// <exception cref="InvalidOperationException">Throws when the server is already running.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the server.  Returns upon the completion of the stop process.
    /// </summary>
    Task StopAsync();
    
    
}

internal interface INexusServer<TClientProxy> : INexusServer, IAcceptsExternalTransport
    where TClientProxy : ProxyInvocationBase, IProxyInvoker, new()
{
    /// <summary>
    /// Cache for all the server nexus contexts.
    /// </summary>
    ConcurrentBag<ServerNexusContext<TClientProxy>> ServerNexusContextCache { get; }
}
