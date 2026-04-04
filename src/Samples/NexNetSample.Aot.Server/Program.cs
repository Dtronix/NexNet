using System.Net;
using NexNet.Logging;
using NexNet.Transports;
using NexNetSample.Aot.Server;

var serverConfig = new TcpServerConfig
{
    EndPoint = new IPEndPoint(IPAddress.Loopback, 2345),
    Logger = new ConsoleLogger { MinLogLevel = NexusLogLevel.Information }
};

var server = ServerNexus.CreateServer(serverConfig, () => new ServerNexus());

Console.WriteLine("[Server] Starting NexNet AOT server on port 2345...");
await server.StartAsync();
Console.WriteLine("[Server] Server started. Press Enter to stop.");

Console.ReadLine();

await server.StopAsync();
Console.WriteLine("[Server] Server stopped.");
