using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.WebSocket;

namespace NexNetDemo.Websocket.Client;

internal class Program
{
    static async Task Main(string[] args)
    {
        //var clientConfig = new WebSocketClientConfig()
        //{
        //    Url = new Uri("ws://192.168.2.110:15050/ws"),
        //    Logger = new ConsoleLogger(),
        //    Timeout = 4000,
        //    PingInterval = 2000,
        //    ReconnectionPolicy = new DefaultReconnectionPolicy()
        //};
        //var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
        
        var clientConfig = new HttpSocketClientConfig()
        {
            Url = new Uri("http://127.0.0.1:15050/httpsocket"),
            Logger = new ConsoleLogger(),
            Timeout = 4000,
            PingInterval = 2000,
            ReconnectionPolicy = new DefaultReconnectionPolicy()
        };
        var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

        await client.ConnectAsync();
        var val = 1;

        //val = await client.Proxy.ServerTaskValueWithParam(val);
        
        Console.ReadLine();
    }
}
