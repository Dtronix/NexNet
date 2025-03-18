using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using NexNet.Logging;

namespace NexNet.Transports.WebSocket.Asp;

/// <summary>
/// Extension methods to simplify adding NexNet servers to ASP.
/// </summary>
public static class NexNetMiddlewareExtensions
{
    
    /// <summary>
    /// Adds the required middleware for a NexNet WebSocket server and starts the server listening.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="server">NexNet server.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder UseNexNetWebSockets(this WebApplication app, INexusServer server, WebSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);
        
        if(server.IsStarted)
            throw new InvalidOperationException("The server is already running.");
        
        _ = Task.Run(async () => await server.StartAsync(app.Lifetime.ApplicationStopping));

        app.UseMiddleware<WebSocketMiddleware>();
        app.Use(Middleware);
        return app;
        
        async Task Middleware(HttpContext context, RequestDelegate next)
        {
            if (config.IsAccepting 
                && context.WebSockets.IsWebSocketRequest 
                && context.Request.Path.Value == config.Path)
            {
                using var websocket = await context.WebSockets.AcceptWebSocketAsync();
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

                await pipe.RunAsync(context.RequestAborted);
            }
            else
            {
                await next(context);
            }
        }
    }

}
