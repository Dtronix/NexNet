using NexNet;
using NexNet.Pipes;
using NexNet.Pipes.Channels;

namespace NexNetDemo.Samples.Channel;

interface IChannelSampleClientNexus
{
}

interface IChannelSampleServerNexus
{
    //ValueTask IntegerChannel(INexusDuplexUnmanagedChannel<int> channel);
    //ValueTask StructChannel(INexusDuplexUnmanagedChannel<ChannelSampleStruct> channel);
    ValueTask ClassChannel(INexusDuplexChannel<ComplexMessage> channel);
    ValueTask ClassChannelBatch(INexusDuplexChannel<ComplexMessage> channel);
    ValueTask DifferentTypesChannel(INexusDuplexPipe pipe);
}


[Nexus<IChannelSampleClientNexus, IChannelSampleServerNexus>(NexusType = NexusType.Client)]
partial class ChannelSampleClientNexus
{

}

[Nexus<IChannelSampleServerNexus, IChannelSampleClientNexus>(NexusType = NexusType.Server)]
partial class ChannelSampleServerNexus
{
    /*public async ValueTask IntegerChannel(INexusDuplexUnmanagedChannel<int> channel)
    {
        var writer = await channel.GetWriterAsync();
        var count = 0;
        while(!writer.IsComplete)
        {
            await writer.WriteAsync(count++);
            await Task.Delay(10);
        }
    }

    public async ValueTask StructChannel(INexusDuplexUnmanagedChannel<ChannelSampleStruct> channel)
    {
        var writer = await channel.GetWriterAsync();
        var count = 0;
        while (!writer.IsComplete)
        {
            await writer.WriteAsync(new ChannelSampleStruct()
            {
                Id = count++,
                Counts = count * 2
            });
            await Task.Delay(10);

        }
    }*/

    public async ValueTask ClassChannel(INexusDuplexChannel<ComplexMessage> channel)
    {
        var writer = await channel.GetWriterAsync();
        var count = 0;
        while (!writer.IsComplete)
        {
            await writer.WriteAsync(ComplexMessage.Random());
            await Task.Delay(10);

            if (++count >= 100)
                return;
        }
    }

    public async ValueTask ClassChannelBatch(INexusDuplexChannel<ComplexMessage> channel)
    {
        await channel.WriteAndComplete(GetComplexMessages());
    }

    public async ValueTask DifferentTypesChannel(INexusDuplexPipe pipe)
    {
        var reader = await pipe.GetChannelReader<long>();

        var readValues = await reader.ReadUntilComplete();

        var writer = await pipe.GetChannelWriter<string>();

        foreach (var readValue in readValues)
        {
            await writer.WriteAsync("Squared Value: " + Math.Pow(readValue, 2));
        }

        await writer.CompleteAsync();
    }

    private static IEnumerable<ComplexMessage> GetComplexMessages()
    {
        var count = 0;
        while (true)
        {
            yield return ComplexMessage.Random();
            if (++count >= 1000)
                yield break;
        }
    }
}
