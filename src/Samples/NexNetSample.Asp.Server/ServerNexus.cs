using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using NexNet;
using NexNet.Collections.Lists;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Pipes;
using NexNetSample.Asp.Shared;

namespace NexNetSample.Asp.Server;

[Nexus<IServerNexusV2, IClientNexus>(NexusType = NexusType.Server, Versioning = NexusVersioning.Negotiation)]
public partial class ServerNexus
{
    public async ValueTask CalculateNumber(INexusDuplexPipe pipe)
    {
        var reader = await pipe.GetUnmanagedChannelReader<int>();
        var writer = await pipe.GetUnmanagedChannelWriter<int>();
    
        // Use IAsyncEnumerable for simple processing
        await foreach (var number in reader)
        {
            var squared = number * number;
            await writer.WriteAsync(squared);
        }
    }
}

