using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using NexNet.Logging;
using NexNet.Transports.Asp.Http;
using NexNet.Transports.Asp.WebSocket;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;

namespace NexNet.Transports.Asp;

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
        
        _ = Task.Run(async () => await server.StartAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false));

        app.UseMiddleware<WebSocketMiddleware>();
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
    /// Adds the required middleware for a NexNet WebSocket server and starts the server listening.
    /// </summary>
    /// <param name="app">Web app to bind the NexNet server to.</param>
    /// <param name="server">NexNet server.</param>
    /// <param name="config">NexNet configurations.</param>
    /// <returns>Web app.</returns>
    public static IApplicationBuilder UseNexNetHttpSockets(this WebApplication app, INexusServer server, HttpSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);
        
        if(server.IsStarted)
            throw new InvalidOperationException("The server is already running.");
        
        _ = Task.Run(async () => await server.StartAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false));
        
        app.Use(Middleware);
        return app;
        
        async Task Middleware(HttpContext context, RequestDelegate next)
        {
            if (config.IsAccepting
                && context.Request.Path.Value == config.Path
                && ValidateHttpConnectionHeaders(context.Request.Headers))
            {
                var tcs = new TaskCompletionSource();
                var pipe = new HttpSocketDuplexPipe(context.Request.BodyReader, context.Response.BodyWriter, tcs);

                int count = 1;
                // Loop until we enqueue the connection.
                while (!config.ConnectionQueue.Post(new HttpSocketAcceptedConnection(context, pipe)))
                {
                    config.Logger?.LogInfo($"Failed to post connection to queue {count++} times.");
                }

                await tcs.Task.ConfigureAwait(false);
            }
            else
            {
                await next(context).ConfigureAwait(false);
            }
        }

        static bool ValidateHttpConnectionHeaders(IHeaderDictionary headers)
        {
            if (headers.Connection.Any(h => h?.Equals("Upgrade") != true))
                return false;
            
            if (headers.Upgrade.Any(h => h?.Equals("NexNet-httpsockets") != true))
                return false;

            return true;
        }
    }

}
