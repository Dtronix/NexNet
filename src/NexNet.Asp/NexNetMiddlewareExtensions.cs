using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Logging;
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
    /// <param name="server">NexNet server.</param>
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
    /// <returns>Web app.</returns>
    public static IApplicationBuilder MapHttpSocketNexus(this WebApplication app, HttpSocketServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);

        return app.Use(async (context, next) =>
        {
            if (config.IsAccepting &&
                context.Request.Path == config.Path)
            {
                var httpSocket = context.Features.Get<IHttpSocketFeature>();
                
                if (httpSocket?.IsHttpSocketRequest == true)
                {
                    var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();

                    var pipe = await httpSocket.AcceptAsync();
                    
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
