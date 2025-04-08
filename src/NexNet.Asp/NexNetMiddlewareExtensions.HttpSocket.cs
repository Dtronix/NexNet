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
    /// method.
    /// </remarks>
    /// <returns>Web app builder.</returns>
    public static IApplicationBuilder MapHttpSocketNexus<TServerNexus, TClientProxy>(
        this WebApplication app,
        HttpSocketServerConfig config,
        NexusServer<TServerNexus, TClientProxy>? server = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);

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
                    await next(context);
                    return;
                }

                if (!await ApplyAuthentication(context, config).ConfigureAwait(false))
                    return;
                
                var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopped, lifetime.ApplicationStopping, context.RequestAborted);
                var pipe = await httpSocket.AcceptAsync().ConfigureAwait(false);
                    
                await Unsafe.As<IAcceptsExternalTransport>(server).AcceptTransport(new HttpSocketTransport(pipe), cts.Token).ConfigureAwait(false);
                return;
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
        Action<HttpSocketConfigure>? configure = null)
        where TServerNexus : ServerNexusBase<TClientProxy>, IInvocationMethodHash
        where TClientProxy : ProxyInvocationBase, IInvocationMethodHash, new()
    {
        ArgumentNullException.ThrowIfNull(app);

        var server = app.Services.GetRequiredService<NexusServer<TServerNexus, TClientProxy>>();

        if (server.IsConfigured)
            throw new InvalidOperationException("Server has already been configured and can not be reused.");

        // If the server is already started, then we can't start it again, and we can't map the same
        // nexus to multiple path endpoints.
        if (server.State == NexusServerState.Running)
            throw new InvalidOperationException("Server is already running.");
        
        // Setup logging
        var logger = app.Services.GetRequiredService<ILogger<INexusServer>>();
        
        var config = new HttpSocketServerConfig
        {
            Logger = new NexusILoggerBridgeLogger(logger)
        };
        
        var options = new HttpSocketOptions();
        
        configure?.Invoke(new HttpSocketConfigure(config, options));
        
        if(string.IsNullOrWhiteSpace(config.Path))
            throw new InvalidOperationException("Configured path is empty.  Must provide a endpoint for mapping to.");
        
        server.Configure(config, () => app.Services.GetRequiredService<TServerNexus>());
        
        // Enable usage of sockets and register this nexus 
        app.UseHttpSockets(options);
        app.MapHttpSocketNexus<TServerNexus, TClientProxy>(config);

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
