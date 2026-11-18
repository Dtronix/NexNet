using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Collections;
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
    /// Endpoints the server accepts sessions on: the primary one (named after its transport, for example
    /// <c>tcp</c>) first, then those added with <see cref="AddListener"/> and <see cref="AddExternalEndpoint"/>, in order.
    /// Empty until the server is configured.
    /// </summary>
    IReadOnlyList<NexusEndpointInfo> Endpoints { get; }

    /// <summary>
    /// Adds a listener endpoint (TCP, TLS, UDS, QUIC, ...) that the server binds on start, with its own session
    /// settings. Only allowed after configuration and while the server is stopped.
    /// </summary>
    /// <param name="config">A listener-mode configuration.</param>
    /// <param name="name">Endpoint name, unique within the server.</param>
    void AddListener(ServerConfig config, string name);

    /// <summary>
    /// Adds an external endpoint whose transports are passed to the returned acceptor (NexNet.Asp does this for
    /// WebSocket and HttpSocket). Only allowed after configuration and while the server is stopped.
    /// </summary>
    /// <param name="config">The endpoint's configuration; its session settings apply to its sessions.</param>
    /// <param name="name">Endpoint name, unique within the server.</param>
    /// <returns>Accepts transports for the endpoint.</returns>
    IAcceptsExternalTransport AddExternalEndpoint(ServerConfig config, string name);

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

// ReSharper disable once UnusedTypeParameter
internal interface INexusServer<TServerNexus, TClientProxy> :INexusServer, IAcceptsExternalTransport
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    /// <summary>
    /// Provider for nexus contexts which allows for external communication with clients.
    /// </summary>
    public ServerNexusContextProvider<TServerNexus, TClientProxy> ContextProvider { get; }
}
