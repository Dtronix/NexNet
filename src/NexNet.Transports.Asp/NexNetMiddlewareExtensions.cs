using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using NexNet.Logging;
using NexNet.Transports.Asp.HttpSocket;
using NexNet.Transports.Asp.WebSocket;
using NexNet.Transports.WebSocket;

namespace NexNet.Transports.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static class NexNetMiddlewareExtensions
{
    /// <summary>
    /// Maps a Nexus on a WebSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="server">NexNet server.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapWebSocketNexus(this WebApplication app, INexusServer server, WebSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);
        
        if(!server.IsStarted)
            throw new InvalidOperationException("The server is required to be started.");

        app.Use(Middleware);
        return app;
        
        async Task Middleware(HttpContext context, RequestDelegate next)
        {
            if (config.IsAccepting 
                && context.WebSockets.IsWebSocketRequest 
                && context.Request.Path.Value == config.Path)
            {
                using var websocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                using var pipe = IWebSocketPipe.Create(websocket, new WebSocketPipeOptions()
                {
                    CloseWhenCompleted = true,
                });

                int count = 1;
                // Loop until we enqueue the connection.
                while(!config.ConnectionQueue.Post(new WebSocketAcceptedConnection(context, pipe)))
                {
                    config.Logger?.LogInfo($"Failed to post connection to queue {count++} times.");
                }

                await pipe.RunAsync(context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await next(context).ConfigureAwait(false);
            }
        }
    }
    
    /// <summary>
    /// Maps a Nexus on a HttpSocket.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="server">NexNet server.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapHttpSocketNexus(this WebApplication app, INexusServer server, HttpSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(config);
        
        if(!server.IsStarted)
            throw new InvalidOperationException("The server is required to be started.");

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path == config.Path)
            {
                var httpSocket = context.Features.Get<IHttpSocketFeature>();
                if (httpSocket?.IsHttpSocketRequest == true)
                {
                    var pipe = await httpSocket.AcceptAsync();
                    config.PushNewConnectionAsync(context, pipe);
                    await pipe.PipeClosedCompletion.ConfigureAwait(false);
                }
            }
            
            await next(context);

        });
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

}
