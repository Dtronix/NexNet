using System.Net;
using NexNet;
using NexNet.Transports;

namespace NexNetDemo.Samples.Versioning;


// V1 Server Interface - Initial version with one method
[NexusVersion(Version = "v1.0", HashLock = -2031775281)]
public interface IVersioningNexusServerV1
{
    [NexusMethod(1)]
    ValueTask<bool> GetStatus();
}

// V1 Server Implementation
[Nexus<IVersioningNexusServerV1, IVersioningNexusClient>(NexusType = NexusType.Server)]
public partial class NexusServerV1
{
    public ValueTask<bool> GetStatus()
    {
        return ValueTask.FromResult(true);
    }
}

// V2 Server Interface - Inherits from V1 and adds new method
[NexusVersion(Version = "v2.0", HashLock = -1210855623)]
public interface IVersioningNexusServerV2 : IVersioningNexusServerV1
{
    [NexusMethod(2)]
    ValueTask<string> GetServerInfo();
}

// V2 Server Implementation
[Nexus<IVersioningNexusServerV2, IVersioningNexusClient>(NexusType = NexusType.Server)]
public partial class VersioningNexusServerV2
{
    // Implements V1 method
    public ValueTask<bool> GetStatus()
    {
        return ValueTask.FromResult(true);
    }

    // New V2 method
    public ValueTask<string> GetServerInfo()
    {
        return ValueTask.FromResult("Server v2.0");
    }
}

// Client interface (same for both versions)
public interface IVersioningNexusClient
{
    ValueTask OnServerMessage(string message);
}

// V1 Client (can only connect to servers supporting V1)
[Nexus<IVersioningNexusClient, IVersioningNexusServerV1>(NexusType = NexusType.Client)]
public partial class VersioningNexusClientV1
{
    public ValueTask OnServerMessage(string message)
    {
        Console.WriteLine($"Received: {message}");
        return ValueTask.CompletedTask;
    }
}

// V2 Client (can connect to servers supporting V2)
[Nexus<IVersioningNexusClient, IVersioningNexusServerV2>(NexusType = NexusType.Client)]
public partial class VersioningNexusClientV2
{
    public ValueTask OnServerMessage(string message)
    {
        Console.WriteLine($"Received: {message}");
        return ValueTask.CompletedTask;
    }
}

public class VersioningSample : INexusSample
{
    public async Task Run()
    {
        var serverConfig = new TcpServerConfig
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1234)
        };
        
        // Server supporting both V1 and V2 clients
        var server = VersioningNexusServerV2.CreateServer(serverConfig, () => new VersioningNexusServerV2());
        await server.StartAsync();

        // V1 Client connecting to server
        var clientConfig = new TcpClientConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 1234)
        };
        
        var clientV1 = VersioningNexusClientV1.CreateClient(clientConfig, new VersioningNexusClientV1());
        var result = await clientV1.TryConnectAsync();

        if (result.Success)
        {
            // Can call V1 methods
            Console.WriteLine(await clientV1.Proxy.GetStatus());
            // Cannot call V2 methods
        }

        // V2 Client connecting to server
        var clientV2 = VersioningNexusClientV2.CreateClient(clientConfig, new VersioningNexusClientV2());
        var result2 = await clientV2.TryConnectAsync();

        if (result2.Success)
        {
            // Can call both V1 and V2 methods
            Console.WriteLine(await clientV2.Proxy.GetStatus());
            Console.WriteLine(await clientV2.Proxy.GetServerInfo());
        }
    }
}
