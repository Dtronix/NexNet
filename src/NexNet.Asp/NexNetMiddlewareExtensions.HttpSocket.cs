using System;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexNet.Asp.HttpSocket;
using NexNet.Collections;
using NexNet.Invocation;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using ArgumentNullException = System.ArgumentNullException;

namespace NexNet.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static partial class NexNetMiddlewareExtensions
{
    /// <summary>
    /// Maps a Nexus on a HttpSocket.  Use <see cref="UseHttpSocketNexusServerAsync{TServerNexus,TClientProxy}"/> over this method.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <param name="server"> Optional server to pass use directly.  Normal usage will leave this null. </param>
    /// <remarks>
    /// This <see cref="MapHttpSocketNexus{TServerNexus,TClientProxy}"/> method is used for setting up mapping of the
    /// passed or nexus server service.  Using this method is discouraged over the complete <see cref="UseHttpSocketNexusServerAsync{TServerNexus,TClientProxy}"/>
    /// method as additional configurations will be required if only this method is used.
    /// When <paramref name="config"/> is the server's primary configuration, its sessions are the primary endpoint's.
    /// Otherwise, when the server is already configured, <paramref name="config"/> is added as an external endpoint
    /// (named after its transport) and its session settings apply to its sessions; see
    /// <see cref="MapNexusHttpSocket{TServerNexus,TClientProxy}"/>.
    /// </remarks>
    /// <returns>Web app builder.</returns>
    public static IApplicationBuilder MapHttpSocketNexus<TServerNexus, TClientProxy>(
        this WebApplication app,
        HttpSocketServerConfig config,
        NexusServer<TServerNexus, TClientProxy>? server = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);

        return MapHttpSocketCore(app, config, server, name: null, requireConfigured: false);
    }

    /// <summary>
    /// Maps an endpoint of <paramref name="server"/> on a HttpSocket at <see cref="AspServerConfig.Path"/>. The server
    /// must be configured and stopped. Unless <paramref name="config"/> is the server's primary configuration, it is
    /// added with <see cref="NexusServer{TServerNexus,TClientProxy}.AddExternalEndpoint"/>, so its session settings
    /// (timeouts, message size, flow control, <c>Authenticate</c>, ASP.NET authentication, proxy headers) apply to the
    /// sessions it accepts while the server's other endpoints keep theirs.
    /// </summary>
    /// <param name="app">Web app to bind the endpoint to. Requires <c>app.UseHttpSockets()</c>.</param>
    /// <param name="server">The server.</param>
    /// <param name="config">The endpoint's configuration.</param>
    /// <param name="name">Endpoint name, unique within the server; defaults to <c>httpsocket</c>.</param>
    /// <returns>Web app builder.</returns>
    public static IApplicationBuilder MapNexusHttpSocket<TServerNexus, TClientProxy>(
        this WebApplication app,
        NexusServer<TServerNexus, TClientProxy> server,
        HttpSocketServerConfig config,
        string? name = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(config);

        return MapHttpSocketCore(app, config, server, name, requireConfigured: true);
    }

    private static IApplicationBuilder MapHttpSocketCore<TServerNexus, TClientProxy>(
        WebApplication app,
        HttpSocketServerConfig config,
        NexusServer<TServerNexus, TClientProxy>? server,
        string? name,
        bool requireConfigured)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var endpoint = ResolveEndpoint(app, config, ref server, name, requireConfigured);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path == config.Path)
            {
                // Either use the server passed or create a new server service.
                server ??= app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();
                
                var httpSocket = context.Features.Get<IHttpSocketFeature>();
                
                // Check to see if the server is running and if this is the right type of connection.
                if (server.State != NexusServerState.Running
                    || httpSocket?.IsHttpSocketRequest != true)
                {
                    await next(context).ConfigureAwait(false);
                    return;
                }

                if (!await ApplyAuthentication(context, config, config.AccessTokenQueryParameter).ConfigureAwait(false))
                    return;
                
                var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopped, lifetime.ApplicationStopping, context.RequestAborted);
                var pipe = await httpSocket.AcceptAsync().ConfigureAwait(false);

                // Extract remote endpoint info (with proxy header support)
                var (remoteAddress, remotePort) = ProxyHeaderResolver.GetRemoteEndPoint(
                    context,
                    config.TrustProxyHeaders);

                var transport = new HttpSocketTransport(pipe, remoteAddress, remotePort,
                    context.Connection.ClientCertificate, context.User);
                await (endpoint ?? server.PrimaryEndpoint)
                    .AcceptTransport(transport, context.Request.IsHttps, cts.Token).ConfigureAwait(false);
                return;
            }
            
            await next(context).ConfigureAwait(false);

        });
    }

    /// <summary>
    /// Uses a Nexus with a HttpSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="configure">Confirmation action for the nexus server config.</param>
    /// <param name="configureCollections">Configures any collections upon starting. Ignored when the server is
    /// already configured.</param>
    /// <returns>Nexus server</returns>
    /// <remarks>
    /// When the server is not configured yet, the HttpSocket configuration becomes its primary configuration. When it
    /// is (for example by <c>AddNexusServer(o =&gt; o.Configure(...))</c> or another <c>Use*NexusServerAsync</c> call),
    /// the HttpSocket is attached as an additional endpoint named <c>httpsocket</c>.
    /// </remarks>
    /// <typeparam name="TServerNexus">Server to map</typeparam>
    /// <typeparam name="TClientProxy">Client proxy to map.</typeparam>
    public static NexusServer<TServerNexus, TClientProxy> UseHttpSocketNexusServerAsync<TServerNexus, TClientProxy>(
        this WebApplication app, 
        Action<HttpSocketConfigure>? configure = null,
        Action<TServerNexus>? configureCollections = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash, ICollectionConfigurer
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);

        var server = app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();

        // Endpoints can only be added to a stopped server.
        if (server.State != NexusServerState.Stopped)
            throw new InvalidOperationException("Server is already running.");
        
        // Setup logging
        var logger = app.Services.GetRequiredService<ILogger<INexusServer>>();
        
        var config = new HttpSocketServerConfig
        {
            Logger = new NexusILoggerBridgeLogger(logger),
            MeterFactory = app.Services.GetService<IMeterFactory>()
        };
        
        var options = new HttpSocketOptions();
        
        configure?.Invoke(new HttpSocketConfigure(config, options));
        
        if(string.IsNullOrWhiteSpace(config.Path))
            throw new InvalidOperationException("Configured path is empty.  Must provide a endpoint for mapping to.");
        
        // Configure the server, or attach to the one already configured.
        if (!server.IsConfigured)
            server.Configure(config, () => app.Services.GetRequiredService<TServerNexus>(), configureCollections);
        
        // Enable usage of sockets and register this nexus 
        app.UseHttpSockets(options);
        app.MapHttpSocketNexus(config, server);

        return server;
    }
    
    /// <summary>
    /// Configuration object for Nexus HttpSockets.
    /// </summary>
    /// <param name="NexusConfig">Nexus configurations.</param>
    /// <param name="SocketOptions">Socket options.</param>
    public record HttpSocketConfigure(HttpSocketServerConfig NexusConfig, HttpSocketOptions SocketOptions);
    
    /// <summary>
    /// Adds the required middleware for a HttpSocket used by NexNet 
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="configure">Optional configuration options.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder UseHttpSockets(this WebApplication app, Action<HttpSocketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new HttpSocketOptions();
        
        configure?.Invoke(options);
        return UseHttpSockets(app, options);
    }

    /// <summary>
    /// Adds the required middleware for a HttpSocket used by NexNet 
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="options">Options for the socket connection.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder UseHttpSockets(this WebApplication app, HttpSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<HttpSocketMiddleware>(options);
    }
}
