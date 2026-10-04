using NexNet.Serialization;

namespace NexNetDemo.Samples.Channel;

[NexusObject]
public struct ChannelSampleStruct
{
    [NexusKey(0)] public int Id { get; set; }
    [NexusKey(1)] public long Counts { get; set; }

    public override string ToString()
    {
        return $"Id: {Id}, Counts: {Counts}";
    }
}
