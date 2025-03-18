using NexNet.Logging;
using NexNet.Transports.WebSocket.Asp;

namespace NexNetDemo.Websocket.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAuthorization();
        var app = builder.Build();
        
        app.MapGet("/", () => "Hello World!");
        var logger = new ConsoleLogger();
        var serverConfig = new WebSocketServerConfig()
        {
            Logger = logger.CreatePrefixedLogger(null, "Server"),
            Timeout = 20000,
        };

        var nexusServer = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());

        app.UseAuthorization();
        app.UseNexNetWebSockets(nexusServer, serverConfig);
        
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
