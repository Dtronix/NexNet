using System.Net.Http.Headers;
using NexNet.Logging;
using NexNet.Pipes;
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
        
        var client = ClientNexus.CreateClient(clientHttpSocketConfig, new ClientNexus());
        await client.ConnectAsync();

        var pipe = client.CreatePipe();
        await client.Proxy.CalculateNumber(pipe);

        var unmanagedReader = await pipe.GetChannelReader<int>();
        var unmanagedWriter = await pipe.GetChannelWriter<int>();

        await unmanagedWriter.WriteAsync(10);
        await unmanagedWriter.WriteAsync(100);
        await unmanagedWriter.WriteAsync(1000);

        var count = 0;
        await foreach (var readInt in unmanagedReader)
        {
            Console.WriteLine(readInt);
            if(++count == 3)
                break;
        }

        await pipe.CompleteAsync();

        // INexusList
        await client.Proxy.IntegerList.ConnectAsync();

        for (int i = 0; i < 10; i++)
        {
            await client.Proxy.IntegerList.AddAsync(i);
        }
        
        
    }
}
