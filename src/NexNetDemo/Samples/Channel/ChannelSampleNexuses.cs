using NexNet;
using NexNet.Pipes;

namespace NexNetDemo.Samples.Channel;

interface IChannelSampleClientNexus
{
}

interface IChannelSampleServerNexus
{
    ValueTask IntegerChannel(INexusDuplexUnmanagedChannel<int> channel);
    ValueTask StructChannel(INexusDuplexUnmanagedChannel<ChannelSampleStruct> channel);
    ValueTask ClassChannel(INexusDuplexChannel<ComplexMessage> channel);
}


[Nexus<IChannelSampleClientNexus, IChannelSampleServerNexus>(NexusType = NexusType.Client)]
partial class ChannelSampleClientNexus
{

}

[Nexus<IChannelSampleServerNexus, IChannelSampleClientNexus>(NexusType = NexusType.Server)]
partial class ChannelSampleServerNexus
{
    public async ValueTask IntegerChannel(INexusDuplexUnmanagedChannel<int> channel)
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
    }

    public async ValueTask ClassChannel(INexusDuplexChannel<ComplexMessage> channel)
    {
        var writer = await channel.GetWriterAsync();
        var count = 0;
        while (!writer.IsComplete)
        {
            await writer.WriteAsync(ComplexMessage.Random());
            await Task.Delay(10);

            if (count++ > 100)
                return;
        }
    }
}
