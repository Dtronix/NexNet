using System.Diagnostics;

namespace NexNetDemo.Samples.InvocationSample;

public class InvocationSample : SampleBase
{
    public InvocationSample(TransportMode transportMode = TransportMode.Uds)
        : base(false, transportMode)
    {

    }

    public async Task UpdateInfo()
    {
        var client = InvocationSampleClientNexus.CreateClient(ClientConfig, new InvocationSampleClientNexus());
        var server = InvocationSampleServerNexus.CreateServer(ServerConfig, () => new InvocationSampleServerNexus());
        server.Start();
        await client.ConnectAsync(true);


        while (true)
        {
            await client.Proxy.UpdateInfoAndWait(1, UserStatus.Online, "Custom Status");
        }
    }

}
