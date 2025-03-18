using NexNet.Logging;
using NexNet.Transports.WebSocket;
using NexNet.Transports.WebSocket.Asp;

namespace NexNetDemo.Websocket.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAuthorization();
        var app = builder.Build();
        
        app.MapGet("/", () => "Hello World!");

        var serverConfig = new WebSocketServerConfig()
        {
            Logger = new LoggerNexusBridge(app.Logger),
        };

        var nexusServer = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());

        app.UseAuthorization();
        app.UseNexNetWebSockets(nexusServer, serverConfig);
        
        app.Run();
    }

}
