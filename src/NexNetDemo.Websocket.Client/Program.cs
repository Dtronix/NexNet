using NexNet.Logging;
using NexNet.Transports.WebSocket;

namespace NexNetDemo.Websocket.Client;

internal class Program
{
    static async Task Main(string[] args)
    {
        var clientConfig = new WebSocketClientConfig()
        {
            Url = new Uri("ws://localhost:5000/ws"),
            Logger = new ConsoleLogger()
        };
        var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

        await client.ConnectAsync();

        await client.Proxy.ServerData(new byte[3000]);
        Console.ReadLine();
    }
}
