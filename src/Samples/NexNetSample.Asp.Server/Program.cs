using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NexNet.Logging;
using NexNet.Transports.Asp;
using NexNet.Transports.Asp.HttpSocket;
using NexNet.Transports.Asp.WebSocket;

namespace NexNetSample.Asp.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel((context, serverOptions) =>
        {
            serverOptions.Listen(IPAddress.Any, 15050);
        });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        
        app.UseHttpsRedirection();
        
        var logger = new ConsoleLogger();
        
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
            Path = "/httpsocket"
        };
        
        var webNexusServer = ServerNexus.CreateServer(webServerConfig, () => new ServerNexus());
        await webNexusServer.StartAsync();

        app.UseWebSockets();
        app.MapWebSocketNexus(webNexusServer, webServerConfig);
    }
}
