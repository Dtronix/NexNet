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
using NexNet.Transports.HttpSocket;
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
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapWebSocketNexus<TServerNexus, TClientProxy>(
        this WebApplication app, 
        WebSocketServerConfig config,
        NexusServer<TServerNexus, TClientProxy>? server = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);
        
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
                    await next(context);
                    return;
                }
                
                if (!await ApplyAuthentication(context, config).ConfigureAwait(false))
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
                    var state = Unsafe.As<WebSocketReadingState>(obj)!;
                    await state.Pipe.RunAsync(state.CancellationToken).ConfigureAwait(false);
                }, new WebSocketReadingState(pipe, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                
                await Unsafe.As<IAcceptsExternalTransport>(server).AcceptTransport(new WebSocketTransport(pipe), cts.Token).ConfigureAwait(false);
            }
            else
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }
    
    private record WebSocketReadingState(IWebSocketPipe Pipe, CancellationToken CancellationToken);
    
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
        Action<WebSocketConfigure>? configure = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        var server = app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();

        if (server.IsConfigured)
            throw new InvalidOperationException("Server has already been configured and can not be reused.");

        // If the server is already started, then we can't start it again, and we can't map the same
        // nexus to multiple path endpoints.
        if (server.State == NexusServerState.Running)
            throw new InvalidOperationException("Server is already running.");

        // Setup logging
        var logger = app.Services.GetRequiredService<ILogger<INexusServer>>();

        var config = new WebSocketServerConfig
        {
            Logger = new NexusILoggerBridgeLogger(logger)
        };
        configure?.Invoke(new WebSocketConfigure(config));
        
        if(string.IsNullOrWhiteSpace(config.Path))
            throw new InvalidOperationException("Configured path is empty.  Must provide a endpoint for mapping to.");

        server.Configure(config, () => app.Services.GetRequiredService<TServerNexus>());

        // Enable usage of sockets and register this nexus 
        app.UseWebSockets();
        app.MapWebSocketNexus<TServerNexus, TClientProxy>(config);

        return server;
    }
    
    /// <summary>
    /// Configuration object for Nexus WebSockets.
    /// </summary>
    /// <param name="NexusConfig">Nexus configurations.</param>
    public record WebSocketConfigure(WebSocketServerConfig NexusConfig);
}
