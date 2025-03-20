using NexNet.Pipes;
using System.IO.Pipelines;

namespace NexNetDemo.Samples.DuplexPipe;

public class DuplexPipeSample : SampleBase
{
    public DuplexPipeSample(TransportMode transportMode = TransportMode.Uds) 
        : base(false, transportMode)
    {
    }

    public async Task UploadSample()
    {
        var client = DuplexPipeSampleClientNexus.CreateClient(ClientConfig, new DuplexPipeSampleClientNexus());
        var server = DuplexPipeSampleServerNexus.CreateServer(ServerConfig, () => new DuplexPipeSampleServerNexus());
        await server.StartAsync();
        await client.ConnectAsync();

        // Create the client pipe.
        var pipe = client.CreatePipe();
        await client.Proxy.Upload(pipe);
        await pipe.ReadyTask;

        // 25 Mb of data.  Substitute with a file or your own stream.
        var stream = new MemoryStream(new byte[1024 * 1024 * 25]);

        await stream.CopyToAsync(pipe.Output);
        await pipe.CompleteAsync();
    }

    public async Task DownloadSample()
    {
        var client = DuplexPipeSampleClientNexus.CreateClient(ClientConfig, new DuplexPipeSampleClientNexus());
        var server = DuplexPipeSampleServerNexus.CreateServer(ServerConfig, () => new DuplexPipeSampleServerNexus());
        await server.StartAsync();
        await client.ConnectAsync();

        // Create the client pipe.
        var pipe = client.CreatePipe();
        await client.Proxy.Download(pipe);
        await pipe.ReadyTask;

        // Substitute with your own file/stream.
        var stream = new MemoryStream();

        //await pipe.Input.CopyToAsync(stream);
        // -- OR--
        while (true)
        {
            // Read the data from the input reader.
            var result = await pipe.Input.ReadAsync();

            // Keep reading until the input returns completed.
            if (result.IsCompleted)
                break;

            // Use the buffer to save the file to the stream.
            result.Buffer.CopyTo(stream);

            // Advance the pipe to the end of the buffer.
            pipe.Input.AdvanceTo(result.Buffer.End);
        }

        await pipe.CompleteAsync();
    }

    public async Task UploadDownloadSample()
    {
        var client = DuplexPipeSampleClientNexus.CreateClient(ClientConfig, new DuplexPipeSampleClientNexus());
        var server = DuplexPipeSampleServerNexus.CreateServer(ServerConfig, () => new DuplexPipeSampleServerNexus());
        await server.StartAsync();
        await client.ConnectAsync();

        // Create the client pipe.
        var pipe = client.CreatePipe();
        await client.Proxy.UploadDownload(pipe);
        await pipe.ReadyTask;

        // Substitute with your own file/stream.
        var stream = new MemoryStream(1024 * 1024 * 25);

        await stream.CopyToAsync(pipe.Output);
        await pipe.Output.CompleteAsync();

        // Reset the stream for receiving.
        stream.SetLength(0);

        await pipe.Input.CopyToAsync(stream);
    }
}
