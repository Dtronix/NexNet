using MemoryPack;
using NexNet.Serialization;

namespace NexNet.IntegrationTests.Pipes;

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

    public override bool Equals(object? obj)
    {
        if (obj == null || GetType() != obj.GetType())
        {
            return false;
        }
        var other = (ComplexMessage)obj;
        return Integer == other.Integer &&
               String1 == other.String1 &&
               StringNull == other.StringNull &&
               DateTime == other.DateTime &&
               DateTimeOffset == other.DateTimeOffset &&
               DateTimeOffsetNull == other.DateTimeOffsetNull;
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Integer);
        hashCode.Add(String1);
        hashCode.Add(StringNull);
        hashCode.Add(DateTime);
        hashCode.Add(DateTimeOffset);
        hashCode.Add(DateTimeOffsetNull);
        return hashCode.ToHashCode();
    }

}

[MemoryPackable]
[NexusObject]
public partial class SimpleMessage
{
    [NexusKey(0)] public byte[] Data { get; set; } = null!;
}
