using System.Net;
using NexNet.Logging;
using NexNet.Transports.Asp;
using NexNet.Transports.Asp.Http;
using NexNet.Transports.Asp.WebSocket;

namespace NexNetDemo.Websocket.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel((context, serverOptions) => serverOptions.Listen(IPAddress.Any, 15050));
        builder.Services.AddAuthorization();
        var app = builder.Build();
        
        var logger = new ConsoleLogger();
        //var serverConfig = new WebSocketServerConfig()
        //{
        //    Logger = logger.CreatePrefixedLogger(null, "Server"),
        //    Timeout = 20000,
        //};
        
        var httpServerConfig = new HttpSocketServerConfig()
        {
            Logger = logger.CreatePrefixedLogger(null, "Server"),
            Timeout = 20000,
            Path = "/httpsocket"
        };

        var nexusServer = ServerNexus.CreateServer(httpServerConfig, () => new ServerNexus());

        app.UseAuthorization();
        //app.UseNexNetWebSockets(nexusServer, serverConfig);
        app.UseNexNetHttpSockets(nexusServer, httpServerConfig);
        
        app.RunAsync();
        
        /*

        var clientConfig = new WebSocketClientConfig()
        {
            Url = new Uri("ws://localhost:5000/ws"),
            Logger = logger.CreatePrefixedLogger(null, "Client"),
            Timeout = 10000,
            PingInterval = 1000,
            ReconnectionPolicy = new DefaultReconnectionPolicy()
        };
        var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

        await client.ConnectAsync();
        var val = 1;

        await Task.Delay(3000);
        val = await client.Proxy.ServerTaskValueWithParam(val);*/
        
        Console.ReadLine();
    }

}
