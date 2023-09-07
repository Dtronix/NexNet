using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Pipes;
using NexNet.Quic;

namespace NexNetDemo.Samples.Channel;

public class ChannelSample : SampleBase
{
    public ChannelSample(TransportMode transportMode = TransportMode.Uds)
        : base(false, transportMode)
    {

    }

    public async Task UnmanagedChannelSample()
    {
        var server = ChannelSampleServerNexus.CreateServer(ServerConfig, () => new ChannelSampleServerNexus());
        await server.StartAsync();

        var client = ChannelSampleClientNexus.CreateClient(ClientConfig, new ChannelSampleClientNexus());
        await client.ConnectAsync();

        await using var channel = client.CreateUnmanagedChannel<int>();

        await client.Proxy.IntegerChannel(channel);

        using var reader = await channel.GetReaderAsync();

        while (!reader.IsComplete)
        {
            foreach (var integer in await reader.ReadAsync())
            {
                Console.WriteLine(integer);
            } 
        }
    }

    public async Task ChannelStructSample()
    {
        var server = ChannelSampleServerNexus.CreateServer(ServerConfig, () => new ChannelSampleServerNexus());
        await server.StartAsync();

        var client = ChannelSampleClientNexus.CreateClient(ClientConfig, new ChannelSampleClientNexus());
        await client.ConnectAsync();

        await using var pipe = client.CreateUnmanagedChannel<ChannelSampleStruct>();

        await client.Proxy.StructChannel(pipe);

        using var reader = await pipe.GetReaderAsync();

        while (!reader.IsComplete)
        {
            foreach (var channelSampleStruct in await reader.ReadAsync())
            {
                Console.WriteLine(channelSampleStruct);
            }
        }
    }

    public async Task ClassSample()
    {
        var server = ChannelSampleServerNexus.CreateServer(ServerConfig, () => new ChannelSampleServerNexus());
        await server.StartAsync();

        var client = ChannelSampleClientNexus.CreateClient(ClientConfig, new ChannelSampleClientNexus());
        await client.ConnectAsync();

        await using var pipe = client.CreateChannel<ComplexMessage>();

        await client.Proxy.ClassChannel(pipe);

        using var reader = await pipe.GetReaderAsync();

        while (!reader.IsComplete)
        {
            foreach (var channelSampleStruct in await reader.ReadAsync())
            {
                Console.WriteLine(channelSampleStruct);
            }
        }
    }


}
