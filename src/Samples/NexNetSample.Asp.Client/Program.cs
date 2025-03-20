using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNetSample.Asp.Client;

internal class Program
{
    static async Task Main(string[] args)
    {
        var clientWebSocketConfig = new WebSocketClientConfig()
        {
            Url = new Uri("ws://192.168.2.110:9000/websocket"),
            Logger = new ConsoleLogger(),
            Timeout = 4000,
            PingInterval = 2000,
            ReconnectionPolicy = new DefaultReconnectionPolicy()
        };
        
        var clientHttpSocketConfig = new HttpSocketClientConfig()
        {
            Url = new Uri("https://192.168.2.110:9000/httpsocket"),
            Logger = new ConsoleLogger(),
            Timeout = 4000,
            PingInterval = 2000,
            ReconnectionPolicy = new DefaultReconnectionPolicy()
        };
        var client = ClientNexus.CreateClient(clientHttpSocketConfig, new ClientNexus());

        await client.ConnectAsync();
        var val = 1;

        val = await client.Proxy.ServerTaskValueWithParam(val);
        
        Console.ReadLine();
    }
}
