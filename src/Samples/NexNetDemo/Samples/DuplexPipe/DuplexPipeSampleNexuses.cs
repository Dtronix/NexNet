using System.IO.Pipelines;
using NexNet;
using NexNet.Pipes;

namespace NexNetDemo.Samples.DuplexPipe;

interface IDuplexPipeSampleClientNexus
{

}

interface IDuplexPipeSampleServerNexus
{
    Task Upload(INexusDuplexPipe pipe);
    Task Download(INexusDuplexPipe pipe);
    Task UploadDownload(INexusDuplexPipe pipe);
}

[Nexus<IDuplexPipeSampleClientNexus, IDuplexPipeSampleServerNexus>(NexusType = NexusType.Client)]
partial class DuplexPipeSampleClientNexus
{

}

[Nexus<IDuplexPipeSampleServerNexus, IDuplexPipeSampleClientNexus>(NexusType = NexusType.Server)]
partial class DuplexPipeSampleServerNexus
{
    public async Task Upload(INexusDuplexPipe pipe)
    {
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

        // Process the data in the stream.
    }

    public async Task Download(INexusDuplexPipe pipe)
    {
        // 25 Mb of data.  Substitute with a file or your own stream.
        var stream = new MemoryStream(new byte[1024 * 1024 * 25]);
        await stream.CopyToAsync(pipe.Output);
    }

    public async Task UploadDownload(INexusDuplexPipe pipe)
    {
        // Substitute with your own file/stream.
        var stream = new MemoryStream(new byte[1024 * 1024 * 25]);

        await pipe.Input.CopyToAsync(stream);

        // Process StreamToAndFrom

        // Send the data back.
        await stream.CopyToAsync(pipe.Output);
    }
}
