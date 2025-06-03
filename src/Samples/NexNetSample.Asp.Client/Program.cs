using System.Net.Http.Headers;
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

        var nexus = new ClientNexus {
            ClientTaskWithParamEvent = (clientNexus, i) =>
            {
                Console.WriteLine($"Received param event with value {i}");
                return ValueTask.CompletedTask;
            }
        };

        var client = ClientNexus.CreateClient(clientHttpSocketConfig, nexus);
        
        await client.ConnectAsync();

        await Task.Delay(1000);

        await client.Proxy.IntegerList.ConnectAsync();
        int counter = 10;
        for (int i = 0; i < 100; i++)
        {
            await client.Proxy.IntegerList.AddAsync(counter++);
            await Task.Delay(1);
        }


        while (true)
        {
            Console.ReadLine();
            
            for (int i = 0; i < 10; i++)
            {
                await client.Proxy.IntegerList.AddAsync(counter++);
            }
        }

        
        //var val = await client.Proxy.ServerTaskValueWithParam(124);
        
        Console.ReadLine();
    }
}
