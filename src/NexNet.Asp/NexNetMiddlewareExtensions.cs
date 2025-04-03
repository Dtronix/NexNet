using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNet.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static class NexNetMiddlewareExtensions
{
    /// <summary>
    /// Maps a Nexus on a WebSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapWebSocketNexus(this WebApplication app, WebSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);
        
        return app.Use(async (context, next) =>
        {
            if (config.IsAccepting &&
                context.Request.Path.Value == config.Path &&
                context.WebSockets.IsWebSocketRequest)
            {
                if (!await ApplyAuthentication(context, config).ConfigureAwait(false))
                    return;
                
                using var websocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                using var pipe = IWebSocketPipe.Create(websocket, new WebSocketPipeOptions()
                {
                    CloseWhenCompleted = true,
                });
                
                // If we can't push a new connection to the queue, the server has been stopped and is not
                // accepting any new connections.
                if (!config.PushNewConnectionAsync(pipe))
                    return;
                
                var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopped, lifetime.ApplicationStopping, context.RequestAborted);

                await pipe.RunAsync(cts.Token).ConfigureAwait(false);
            }
            else
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }
    
    /// <summary>
    /// Maps a Nexus on a HttpSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <returns>Web app builder.</returns>
    public static IApplicationBuilder MapHttpSocketNexus(this WebApplication app, HttpSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);

        return app.Use(async (context, next) =>
        {
            if (config.IsAccepting &&
                context.Request.Path == config.Path)
            {
                if (!await ApplyAuthentication(context, config).ConfigureAwait(false))
                    return;
                
                var httpSocket = context.Features.Get<IHttpSocketFeature>();
                
                if (httpSocket?.IsHttpSocketRequest == true)
                {
                    var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();

                    var pipe = await httpSocket.AcceptAsync().ConfigureAwait(false);
                    
                    // If we can't push a new connection to the queue, the server has been stopped and is not
                    // accepting any new connections.
                    if (!config.PushNewConnectionAsync(pipe))
                        return;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopped, lifetime.ApplicationStopping, context.RequestAborted);
                    await pipe.PipeClosedCompletion.WaitAsync(cts.Token).ConfigureAwait(false);
                }
            }
            
            await next(context);

        });
    }

    /// <summary>
    /// Uses a Nexus with a HttpSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="configure">Confirmation action for the nexus server config.</param>
    /// <returns>Nexus server</returns>
    /// <typeparam name="TServerNexus">Server to map</typeparam>
    /// <typeparam name="TClientProxy">Client proxy to map.</typeparam>
    public static NexusServer<TServerNexus, TClientProxy> UseHttpSocketNexusServerAsync<TServerNexus, TClientProxy>(
        this WebApplication app, 
        Action<HttpSocketServerConfig>? configure = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var server = app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();

        if (server.Config != null)
            throw new InvalidOperationException("Server has already been configured and can not be reused.");

        // If the server is already started, then we can't start it again, and we can't map the same
        // nexus to multiple path endpoints.
        if (server.IsStarted)
            throw new InvalidOperationException("Server is already started.");
        
        // Setup logging
        var logger = app.Services.GetRequiredService<ILogger<INexusServer>>();
        
        var config = new HttpSocketServerConfig
        {
            Logger = new NexusILoggerBridgeLogger(logger)
        };
        configure?.Invoke(config);
        
        if(string.IsNullOrWhiteSpace(config.Path))
            throw new InvalidOperationException("Configured path is empty.  Must provide a endpoint for mapping to.");
        
        server.Configure(config, () => app.Services.GetRequiredService<TServerNexus>());
        
        // Enable usage of sockets and register this nexus 
        app.UseHttpSockets();
        app.MapHttpSocketNexus(config);

        return server;
    }
    
    /// <summary>
    /// Uses a Nexus with a HttpSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="configure">Confirmation action for the nexus server config.</param>
    /// <returns>Nexus server</returns>
    /// <typeparam name="TServerNexus">Server to map</typeparam>
    /// <typeparam name="TClientProxy">Client proxy to map.</typeparam>
    public static NexusServer<TServerNexus, TClientProxy> UseWebSocketNexusServerAsync<TServerNexus, TClientProxy>(
        this WebApplication app, 
        Action<WebSocketServerConfig>? configure = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var server = app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();

        if (server.Config != null)
            throw new InvalidOperationException("Server has already been configured and can not be reused.");

        // If the server is already started, then we can't start it again, and we can't map the same
        // nexus to multiple path endpoints.
        if (server.IsStarted)
            throw new InvalidOperationException("Server is already started.");

        // Setup logging
        var logger = app.Services.GetRequiredService<ILogger<INexusServer>>();

        var config = new WebSocketServerConfig
        {
            Logger = new NexusILoggerBridgeLogger(logger)
        };
        configure?.Invoke(config);
        
        if(string.IsNullOrWhiteSpace(config.Path))
            throw new InvalidOperationException("Configured path is empty.  Must provide a endpoint for mapping to.");

        server.Configure(config, () => app.Services.GetRequiredService<TServerNexus>());

        // Enable usage of sockets and register this nexus 
        app.UseWebSockets();
        app.MapWebSocketNexus(config);

        return server;
    }
    
    /// <summary>
    /// Adds a Nexus Server and ClientProxy to the Service collection
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection AddNexusServer<TServerNexus, TClientProxy>(this IServiceCollection services)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        // Add the nexus to the services as a transient service to allow for instancing on each connection
        services.AddTransient<TServerNexus>();
        
        // Adds the server as a singleton to the service collection
        services.AddSingleton(p => 
            new NexusServer<TServerNexus, TClientProxy>());

        return services;
    }

    /// <summary>
    /// Adds the required middleware for a HttpSocket used by NexNet 
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="configure"></param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder UseHttpSockets(this WebApplication app, Action<HttpSocketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new HttpSocketOptions();
        
        configure?.Invoke(options);
        return app.UseMiddleware<HttpSocketMiddleware>(options);
    }
    
    
    
    /// <summary>
    /// Applies the current configured authentication scheme.
    /// </summary>
    /// <param name="context">Http context for the current connection.</param>
    /// <param name="config">Configuration containing the authentication scheme.</param>
    /// <returns>
    /// Returns true if the connection should continue based upon the configurations.
    /// The client may be authenticated or not if configured to disable authentication.
    /// </returns>
    public static async Task<bool> ApplyAuthentication(HttpContext context, AspServerConfig config)
    {
        if (!config.AspEnableAuthentication)
            return true;
        
        var authScheme = config.AspAuthenticationScheme?.Trim();
        AuthenticateResult authResult;
        if (string.IsNullOrWhiteSpace(authScheme))
        {
            authResult = await context.AuthenticateAsync();
        }
        else
        {
            authResult = await context.AuthenticateAsync(authScheme);
        }

        if (authResult.Succeeded)
            return true;

        // Log the failure.
        config.Logger?.LogInfo(authResult.Failure, "Authentication failed");
                    
        // Notify the connecting client with Unauthorized.
        context.Response.StatusCode = 401;
        return false;
    }

}
