using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.Asp;
using NexNet.Transports.Asp.HttpSocket;
using NexNet.Transports.Asp.WebSocket;
using NexNet.Transports.WebSocket;

namespace NexNetDemo.Websocket.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel((context, serverOptions) =>
        {

            serverOptions.Listen(IPAddress.Any, 15050);
            serverOptions.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        
        app.UseHttpsRedirection();
        
        var logger = new ConsoleLogger();
        //var serverConfig = new WebSocketServerConfig()
        //{
        //    Logger = logger.CreatePrefixedLogger(null, "Server"),
        //    Timeout = 20000,
        //};
        


        app.UseAuthorization();
        //app.UseNexNetWebSockets(nexusServer, serverConfig);
        
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
        
        app.RunAsync();
        
        Console.ReadLine();
    }

}
