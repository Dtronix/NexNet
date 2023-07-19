using System.IO.Pipelines;
using NexNet;

namespace NexNetDemo.Samples.DuplexPipe;

interface IDuplexPipeSampleClientNexus
{

}

interface IDuplexPipeSampleServerNexus
{
    ValueTask UploadFile(INexusDuplexPipe pipe);
    ValueTask DownloadFile(INexusDuplexPipe pipe);
}

[Nexus<IDuplexPipeSampleClientNexus, IDuplexPipeSampleServerNexus>(NexusType = NexusType.Client)]
partial class DuplexPipeSampleClientNexus
{

}

[Nexus<IDuplexPipeSampleServerNexus, IDuplexPipeSampleClientNexus>(NexusType = NexusType.Server)]
partial class DuplexPipeSampleServerNexus
{
    public async ValueTask UploadFile(INexusDuplexPipe pipe)
    {
        // Substitute with your own file/stream.
        var stream = new MemoryStream();
        var length = 0L;
        while (true)
        {
            // Read the data from the input reader.
            var result = await pipe.Input.ReadAsync();

            // Keep reading until the input returns completed.
            if (result.IsCompleted)
                break;


            length += result.Buffer.Length;

            // Use the buffer to save the file to the stream.
            result.Buffer.CopyTo(stream);
            pipe.Input.AdvanceTo(result.Buffer.End);
        }

        // Process the data in the stream.
    }

    public async ValueTask DownloadFile(INexusDuplexPipe pipe)
    {
        // 25 Mb of data.  Substitute with a file or your own stream.
        var stream = new MemoryStream(new byte[1024 * 1024 * 25]);
        await stream.CopyToAsync(pipe.Output);
    }
}
