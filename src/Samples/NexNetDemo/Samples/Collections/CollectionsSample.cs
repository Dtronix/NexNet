using NexNet;

namespace NexNetDemo.Samples.Collections;

public class CollectionSample : SampleBase
{
    public CollectionSample(string serverIpAddress)
        : base(false, TransportMode.Uds)
    {
    }

    public async Task RunServer()
    {
        var masterServer = CollectionSampleServerNexus.CreateServer(ServerConfig, () => new CollectionSampleServerNexus());
        await masterServer.StartAsync();

        // Connect the client to the master server
        var client = CollectionSampleClientNexus.CreateClient(ClientConfig, new CollectionSampleClientNexus());
        await client.ConnectAsync();
        
        //ServerConfig.
        
        //var secondaryServer = CollectionSampleServerNexus.CreateServer(ServerConfig, () => new CollectionSampleServerNexus(),
        //    nexus =>
        //    {
        //        await nexus.MainList.ConnectAsync()
        //    });
//
        //if(masterServer.StoppedTask != null)
        //    await masterServer.StoppedTask;
    }

    public async Task RunClient()
    {
        var client = CollectionSampleClientNexus.CreateClient(ClientConfig, new CollectionSampleClientNexus());
        await client.ConnectAsync();
    }


}
