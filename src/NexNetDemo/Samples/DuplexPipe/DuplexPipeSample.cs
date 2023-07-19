using System.IO.Pipelines;
using NexNet;

namespace NexNetDemo.Samples.DuplexPipe;

public class DuplexPipeSample : SampleBase
{
    public DuplexPipeSample() 
        : base(false)
    {
    }

    public async Task UploadSample()
    {
        var client = DuplexPipeSampleClientNexus.CreateClient(ClientConfig, new DuplexPipeSampleClientNexus());
        var server = DuplexPipeSampleServerNexus.CreateServer(ServerConfig, () => new DuplexPipeSampleServerNexus());
        server.Start();
        await client.ConnectAsync();
        await client.ReadyTask!;


        var pipe = client.CreatePipe();
        await client.Proxy.UploadFile(pipe);
        await pipe.ReadyTask;

        // 25 Mb of data.  Substitute with a file or your own stream.
        var stream = new MemoryStream(new byte[1024 * 1024]);

        await stream.CopyToAsync(pipe.Output);
        await pipe.Output.FlushAsync();
        await pipe.CompleteAsync();
    }

    public async Task DownloadSample()
    {
        var client = DuplexPipeSampleClientNexus.CreateClient(ClientConfig, new DuplexPipeSampleClientNexus());
        var server = DuplexPipeSampleServerNexus.CreateServer(ServerConfig, () => new DuplexPipeSampleServerNexus());
        server.Start();
        await client.ConnectAsync();
        await client.ReadyTask!;

        var pipe = client.CreatePipe();
        await client.Proxy.DownloadFile(pipe);
        await pipe.ReadyTask;

        // Substitute with your own file/stream.
        var stream = new MemoryStream();

        while (true)
        {
            // Read the data from the input reader.
            var result = await pipe.Input.ReadAsync();

            // Keep reading until the input returns completed.
            if (result.IsCompleted)
                break;

            // Use the buffer to save the file to the stream.
            result.Buffer.CopyTo(stream);
        }

    }
}
