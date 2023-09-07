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

        var pipe = client.CreatePipe();

        await client.Proxy.IntegerChannel(pipe);

        var reader = await pipe.GetUnmanagedChannelReader<int>();

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

        var pipe = client.CreatePipe();

        await client.Proxy.StructChannel(pipe);

        var reader = await pipe.GetUnmanagedChannelReader<ChannelSampleStruct>();

        while (!reader.IsComplete)
        {
            foreach (var channelSampleStruct in await reader.ReadAsync())
            {
                Console.WriteLine(channelSampleStruct);
            }
        }
    }

    public async Task ClassStructSample()
    {
        var server = ChannelSampleServerNexus.CreateServer(ServerConfig, () => new ChannelSampleServerNexus());
        await server.StartAsync();

        var client = ChannelSampleClientNexus.CreateClient(ClientConfig, new ChannelSampleClientNexus());
        await client.ConnectAsync();

        var pipe = client.CreatePipe();

        await client.Proxy.StructChannel(pipe);

        var reader = await pipe.GetChannelReader<ComplexMessage>();

        while (!reader.IsComplete)
        {
            foreach (var channelSampleStruct in await reader.ReadAsync())
            {
                Console.WriteLine(channelSampleStruct);
            }
        }
    }


}
