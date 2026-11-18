using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet.Asp;

/// <summary>
/// Configures a Nexus Server registered with
/// <see cref="NexNetMiddlewareExtensions.AddNexusServer{TServerNexus,TClientProxy}(IServiceCollection, Action{NexusServerOptions{TServerNexus,TClientProxy}})"/>.
/// </summary>
/// <typeparam name="TServerNexus">Server nexus.</typeparam>
/// <typeparam name="TClientProxy">Client proxy.</typeparam>
public sealed class NexusServerOptions<TServerNexus, TClientProxy>
    where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
    where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
{
    private readonly List<(ServerConfig Config, string Name)> _listeners = new();
    private ServerConfig? _primary;
    private Action<TServerNexus>? _configureCollections;

    /// <summary>
    /// Sets the primary configuration: the server-wide settings and the primary endpoint. When it has no
    /// <see cref="ConfigBase.Logger"/> or <see cref="ConfigBase.MeterFactory"/>, the application's
    /// <c>ILogger</c> and <c>IMeterFactory</c> are used.
    /// </summary>
    /// <param name="primary">Primary configuration. A WebSocket or HttpSocket configuration is served once mapped
    /// with <c>MapWebSocketNexus</c> / <c>MapHttpSocketNexus</c>.</param>
    /// <param name="configureCollections">Configures collections when the server is created.</param>
    /// <returns>These options.</returns>
    public NexusServerOptions<TServerNexus, TClientProxy> Configure(
        ServerConfig primary,
        Action<TServerNexus>? configureCollections = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (_primary != null)
            throw new InvalidOperationException("The primary configuration is already set.");

        _primary = primary;
        _configureCollections = configureCollections;
        return this;
    }

    /// <summary>
    /// Adds a listener endpoint (TCP, TLS, UDS, QUIC, ...); see <see cref="NexusServer{TServerNexus,TClientProxy}.AddListener"/>.
    /// </summary>
    /// <param name="config">A listener-mode configuration.</param>
    /// <param name="name">Endpoint name, unique within the server.</param>
    /// <returns>These options.</returns>
    public NexusServerOptions<TServerNexus, TClientProxy> AddListener(ServerConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _listeners.Add((config, name));
        return this;
    }

    internal void Apply(NexusServer<TServerNexus, TClientProxy> server, IServiceProvider services)
    {
        var primary = _primary ?? throw new InvalidOperationException(
            "AddNexusServer: call Configure(primary) on the options.");

        if (primary.Logger == null)
        {
            var logger = services.GetService<ILogger<INexusServer>>();
            if (logger != null)
                primary.Logger = new NexusILoggerBridgeLogger(logger);
        }

        primary.MeterFactory ??= services.GetService<IMeterFactory>();

        server.Configure(primary, services.GetRequiredService<TServerNexus>, _configureCollections);

        foreach (var (config, name) in _listeners)
            server.AddListener(config, name);
    }
}
