using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexNet.Asp.WebSocket;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNet.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static partial class NexNetMiddlewareExtensions
{
    /// <summary>
    /// Maps a Nexus on a WebSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <param name="server">Optional server to pass use directly.  Normal usage will leave this null.</param>
    /// <remarks>
    /// When <paramref name="config"/> is the server's primary configuration, its sessions are the primary endpoint's.
    /// Otherwise, when the server is already configured, <paramref name="config"/> is added as an external endpoint
    /// (named after its transport) and its session settings apply to its sessions; see
    /// <see cref="MapNexusWebSocket{TServerNexus,TClientProxy}"/>. Requires <c>app.UseWebSockets()</c>.
    /// </remarks>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapWebSocketNexus<TServerNexus, TClientProxy>(
        this WebApplication app, 
        WebSocketServerConfig config,
        NexusServer<TServerNexus, TClientProxy>? server = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);

        return MapWebSocketCore(app, config, server, name: null, requireConfigured: false);
    }

    /// <summary>
    /// Maps an endpoint of <paramref name="server"/> on a WebSocket at <see cref="AspServerConfig.Path"/>. The server
    /// must be configured and stopped. Unless <paramref name="config"/> is the server's primary configuration, it is
    /// added with <see cref="NexusServer{TServerNexus,TClientProxy}.AddExternalEndpoint"/>, so its session settings
    /// (timeouts, message size, flow control, <c>Authenticate</c>, ASP.NET authentication, proxy headers) apply to the
    /// sessions it accepts while the server's other endpoints keep theirs.
    /// </summary>
    /// <param name="app">Web app to bind the endpoint to. Requires <c>app.UseWebSockets()</c>.</param>
    /// <param name="server">The server.</param>
    /// <param name="config">The endpoint's configuration.</param>
    /// <param name="name">Endpoint name, unique within the server; defaults to <c>websocket</c>, or <c>websocket</c>
    /// followed by the path when that name is taken.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapNexusWebSocket<TServerNexus, TClientProxy>(
        this WebApplication app,
        NexusServer<TServerNexus, TClientProxy> server,
        WebSocketServerConfig config,
        string? name = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(config);

        return MapWebSocketCore(app, config, server, name, requireConfigured: true);
    }

    private static IApplicationBuilder MapWebSocketCore<TServerNexus, TClientProxy>(
        WebApplication app,
        WebSocketServerConfig config,
        NexusServer<TServerNexus, TClientProxy>? server,
        string? name,
        bool requireConfigured)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var endpoint = ResolveEndpoint(app, config, ref server, name, requireConfigured);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value == config.Path &&
                context.WebSockets.IsWebSocketRequest)
            {
                // Either use the server passed or create a new server service.
                server ??= app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();
                
                // Check to see if the server is running.
                if (server.State != NexusServerState.Running)
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }
                
                if (!IsOriginAllowed(context, config.AllowedOrigins))
                {
                    config.Logger?.LogDebug($"WebSocket request origin {context.Request.Headers.Origin} is not in the list of allowed origins.");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (!await ApplyAuthentication(context, config, config.AccessTokenQueryParameter).ConfigureAwait(false))
                    return;
                
                var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopped, lifetime.ApplicationStopping, context.RequestAborted);
                
                using var websocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                using var pipe = IWebSocketPipe.Create(websocket, new WebSocketPipeOptions()
                {
                    CloseWhenCompleted = true,
                }, config);
                
                // Run the reader until the connection completes.
                _ = Task.Factory.StartNew(static async obj =>
                {
                    var state = (WebSocketReadingState)obj!;
                    await state.Pipe.RunAsync(state.CancellationToken).ConfigureAwait(false);
                }, new WebSocketReadingState(pipe, cts.Token), cts.Token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

                // Extract remote endpoint info (with proxy header support)
                var (remoteAddress, remotePort) = ProxyHeaderResolver.GetRemoteEndPoint(
                    context,
                    config.TrustProxyHeaders);

                var transport = new WebSocketTransport(pipe, remoteAddress, remotePort,
                    context.Connection.ClientCertificate, context.User);
                await (endpoint ?? server.PrimaryEndpoint)
                    .AcceptTransport(transport, context.Request.IsHttps, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }
    
    private record WebSocketReadingState(IWebSocketPipe Pipe, CancellationToken CancellationToken);

    /// <summary>
    /// Uses a Nexus with a WebSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="configure">Confirmation action for the nexus server config.</param>
    /// <param name="configureCollections">Configures any collections upon starting. Ignored when the server is
    /// already configured.</param>
    /// <returns>Nexus server</returns>
    /// <remarks>
    /// When the server is not configured yet, the WebSocket configuration becomes its primary configuration. When it is
    /// (for example by <c>AddNexusServer(o => o.Configure(...))</c> or another <c>Use*NexusServerAsync</c> call),
    /// the WebSocket is attached as an additional endpoint named <c>websocket</c>.
    /// </remarks>
    /// <typeparam name="TServerNexus">Server to map</typeparam>
    /// <typeparam name="TClientProxy">Client proxy to map.</typeparam>
    public static NexusServer<TServerNexus, TClientProxy> UseWebSocketNexusServerAsync<TServerNexus, TClientProxy>(
        this WebApplication app, 
        Action<WebSocketConfigure>? configure = null,
        Action<TServerNexus>? configureCollections = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var server = app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();

        // Endpoints can only be added to a stopped server.
        if (server.State != NexusServerState.Stopped)
            throw new InvalidOperationException("Server is already running.");

        // Setup logging
        var logger = app.Services.GetRequiredService<ILogger<INexusServer>>();

        var config = new WebSocketServerConfig
        {
            Logger = new NexusILoggerBridgeLogger(logger),
            MeterFactory = app.Services.GetService<IMeterFactory>()
        };
        configure?.Invoke(new WebSocketConfigure(config));
        
        if(string.IsNullOrWhiteSpace(config.Path))
            throw new InvalidOperationException("Configured path is empty.  Must provide a endpoint for mapping to.");

        // Configure the server, or attach to the one already configured.
        if (!server.IsConfigured)
            server.Configure(config, () => app.Services.GetRequiredService<TServerNexus>(), configureCollections);

        // Enable usage of sockets and register this nexus 
        var webSocketOptions = new WebSocketOptions();
        foreach (var origin in config.AllowedOrigins)
            webSocketOptions.AllowedOrigins.Add(origin);
        app.UseWebSockets(webSocketOptions);
        app.MapWebSocketNexus(config, server);

        return server;
    }
    
    /// <summary>
    /// Configuration object for Nexus WebSockets.
    /// </summary>
    /// <param name="NexusConfig">Nexus configurations.</param>
    public record WebSocketConfigure(WebSocketServerConfig NexusConfig);
}
