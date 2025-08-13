namespace NexNetDemo.Samples.Collections;

public class CollectionSample : SampleBase
{
    public CollectionSample(string serverIpAddress)
        : base(false, TransportMode.Uds)
    {
    }

    public async Task RunServer()
    {
        var server = CollectionSampleServerNexus.CreateServer(ServerConfig, () => new CollectionSampleServerNexus());
        await server.StartAsync();
        
        var server2 = CollectionSampleServerNexus.CreateServer(ServerConfig, () => new CollectionSampleServerNexus(),
            nexus =>
            {
                var client = CollectionSampleClientNexus.CreateClient(ClientConfig, new CollectionSampleClientNexus());
                await nexus.MainList.ConnectAsync()
            });

        if(server.StoppedTask != null)
            await server.StoppedTask;
    }

    public async Task RunClient()
    {
        var client = CollectionSampleClientNexus.CreateClient(ClientConfig, new CollectionSampleClientNexus());
        await client.ConnectAsync();
    }


}
