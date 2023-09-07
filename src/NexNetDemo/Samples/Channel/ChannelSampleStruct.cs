namespace NexNetDemo.Samples.Channel;

public struct ChannelSampleStruct
{
    public int Id { get; set; }
    public long Counts { get; set; }

    public override string ToString()
    {
        return $"Id: {Id}, Counts: {Counts}";
    }
}
