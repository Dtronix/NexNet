using MemoryPack;

namespace NexNetDemo.Samples.Channel;

[MemoryPackable]
public partial class ComplexMessage
{
    public int Integer { get; set; }
    public string String1 { get; set; } = null!;
    public string? StringNull { get; set; }

    public DateTime DateTime { get; set; }
    public DateTimeOffset DateTimeOffset { get; set; }
    public DateTimeOffset? DateTimeOffsetNull { get; set; }

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
