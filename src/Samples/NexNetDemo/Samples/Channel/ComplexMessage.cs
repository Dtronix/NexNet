using MemoryPack;
using NexNet.Serialization;

namespace NexNetDemo.Samples.Channel;

[MemoryPackable]
[NexusObject]
public partial class ComplexMessage
{
    [NexusKey(0)] public int Integer { get; set; }
    [NexusKey(1)] public string String1 { get; set; } = null!;
    [NexusKey(2)] public string? StringNull { get; set; }

    [NexusKey(3)] public DateTime DateTime { get; set; }
    [NexusKey(4)] public DateTimeOffset DateTimeOffset { get; set; }
    [NexusKey(5)] public DateTimeOffset? DateTimeOffsetNull { get; set; }

    public static ComplexMessage Random()
    {
        return new ComplexMessage()
        {
            Integer = new Random().Next(),
            String1 = Guid.NewGuid().ToString(),
            StringNull = null,
            DateTime = DateTime.Now,
            DateTimeOffset = DateTimeOffset.Now,
            DateTimeOffsetNull = DateTimeOffset.Now
        };
    }
    public override string ToString()
    {
        return $"Integer: {Integer}, String1: {String1}, StringNull: {StringNull}, DateTime: {DateTime}, DateTimeOffset: {DateTimeOffset}, DateTimeOffsetNull: {DateTimeOffsetNull}";
    }
}
