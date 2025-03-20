using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NexNet.Asp;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Logging;

namespace NexNetSample.Asp.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel((context, serverOptions) =>
        {
            serverOptions.Listen(IPAddress.Any, 9000);
        });
        builder.Services.AddAuthorization();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });
        
        var app = builder.Build();
        
        app.UseHttpsRedirection();
        
        var logger = new ConsoleLogger();

        
        app.UseForwardedHeaders();
        app.UseAuthorization();
  
        await MapWebSocket(logger, app);
        await MapHttpSocket(logger, app);

        app.RunAsync();
        
        Console.ReadLine();
    }

    private static async Task MapHttpSocket(ConsoleLogger logger, WebApplication app)
    {
        var httpServerConfig = new HttpSocketServerConfig()
        {
            Logger = logger.CreatePrefixedLogger(null, "Server"),
            Timeout = 20000,
            Path = "/httpsocket"
        };

        var nexusServer = ServerNexus.CreateServer(httpServerConfig, () => new ServerNexus());
        await nexusServer.StartAsync();
        
        app.UseHttpSockets();
        app.MapHttpSocketNexus(nexusServer, httpServerConfig);
    }

    private static async Task MapWebSocket(ConsoleLogger logger, WebApplication app)
    {
        var webServerConfig = new WebSocketServerConfig()
        {
            Logger = logger.CreatePrefixedLogger(null, "Server"),
            Timeout = 20000,
            Path = "/websocket"
        };
        
        var webNexusServer = ServerNexus.CreateServer(webServerConfig, () => new ServerNexus());
        await webNexusServer.StartAsync();

        app.UseWebSockets();
        app.MapWebSocketNexus(webNexusServer, webServerConfig);
    }
}
