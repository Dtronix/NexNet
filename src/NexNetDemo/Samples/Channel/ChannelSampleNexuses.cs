using NexNet;
using NexNet.Pipes;

namespace NexNetDemo.Samples.Channel;

interface IChannelSampleClientNexus
{
}

interface IChannelSampleServerNexus
{
    ValueTask IntegerChannel(INexusDuplexPipe message);
    ValueTask StructChannel(INexusDuplexPipe message);
    ValueTask ClassChannel(INexusDuplexPipe message);
}


[Nexus<IChannelSampleClientNexus, IChannelSampleServerNexus>(NexusType = NexusType.Client)]
partial class ChannelSampleClientNexus
{

}

[Nexus<IChannelSampleServerNexus, IChannelSampleClientNexus>(NexusType = NexusType.Server)]
partial class ChannelSampleServerNexus
{
    public async ValueTask IntegerChannel(INexusDuplexPipe message)
    {
        var channel = await message.GetUnmanagedChannelWriter<int>();
        var count = 0;
        while(!channel.IsComplete)
        {
            await channel.WriteAsync(count++);
            await Task.Delay(10);
        }
    }

    public async ValueTask StructChannel(INexusDuplexPipe message)
    {
        var channel = await message.GetUnmanagedChannelWriter<ChannelSampleStruct>();
        var count = 0;
        while (!channel.IsComplete)
        {
            await channel.WriteAsync(new ChannelSampleStruct()
            {
                Id = count++,
                Counts = count * 2
            });
            await Task.Delay(10);

        }
    }

    public async ValueTask ClassChannel(INexusDuplexPipe message)
    {
        var channel = await message.GetChannelWriter<ComplexMessage>();
        while (!channel.IsComplete)
        {
            await channel.WriteAsync(ComplexMessage.Random());
            await Task.Delay(10);

        }
    }
}
