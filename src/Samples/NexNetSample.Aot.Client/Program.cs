using System.Net;
using NexNet.Logging;
using NexNet.Transports;
using NexNetSample.Aot.Client;

var clientConfig = new TcpClientConfig
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 2345),
    Logger = new ConsoleLogger { MinLogLevel = NexusLogLevel.Information }
};

var client = ClientNexus.CreateClient(clientConfig, new ClientNexus());

Console.WriteLine("[Client] Connecting to server...");
await client.ConnectAsync();
Console.WriteLine("[Client] Connected!");

// Test fire-and-forget
client.Proxy.Ping();
await Task.Delay(100);

// Test method with return value
var info = await client.Proxy.GetServerInfo();
Console.WriteLine($"[Client] Server info: {info}");

// Test method with multiple args and return value
var sum = await client.Proxy.Add(17, 25);
Console.WriteLine($"[Client] 17 + 25 = {sum}");

// Test awaitable method with args (server broadcasts back to all clients)
await client.Proxy.SendMessage("AotUser", "Hello from AOT client!");
await Task.Delay(200);

Console.WriteLine("[Client] All tests complete. Press Enter to disconnect.");
Console.ReadLine();

await client.DisconnectAsync();
