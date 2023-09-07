using MemoryPack;

namespace NexNet.IntegrationTests.Pipes;

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
