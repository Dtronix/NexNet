using System.Net.Http.Headers;
using NexNet;
using NexNet.Logging;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;

namespace NexNetSample.Asp.Client;

internal class Program
{
    static async Task Main(string[] args)
    {
        var clientWebSocketConfig = new WebSocketClientConfig()
        {
            Url = new Uri("ws://127.0.0.1:9001/nexus"),
            Logger = new ConsoleLogger(),
            ReconnectionPolicy = new DefaultReconnectionPolicy(),
            AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "SecretTokenValue")
        };
        
        var clientHttpSocketConfig = new HttpSocketClientConfig()
        {
            Url = new Uri("http://127.0.0.1:9001/nexus"),
            Logger = new ConsoleLogger(),
            ReconnectionPolicy = new DefaultReconnectionPolicy(),
            AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "SecretTokenValue")
        };

        var client = ClientNexus.CreateClient(clientWebSocketConfig, new ClientNexus());

        await client.ConnectAsync();
        
        var val = await client.Proxy.ServerTaskValueWithParam(124);
        
        Console.ReadLine();
    }
}
