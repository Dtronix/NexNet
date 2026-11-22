using System.Numerics;
using NexNet;
using NexNet.Serialization;
using NexNet.TestNexus.Interop;

// A type with a user formatter, mapped to a hand-written formatter in each opted-in language (D12).
[assembly: NexusFormatter<InteropPointFormatter, InteropPoint>]
[assembly: NexusTargetFormatter<InteropPoint>(NexusTargets.Java, "com.dtronix.nexnet.testnexus.InteropPointFormatter")]
[assembly: NexusTargetFormatter<InteropPoint>(NexusTargets.JavaScript, "./interop-point-formatter.js#InteropPointFormatter")]

namespace NexNet.TestNexus.Interop;

// The interop type zoo: every shape of value the cross-language tests send (protocol specification §10). Wire layouts are
// part of the interop contract, so keys, tags and enum values never change; add new members and types instead.

/// <summary>An int-backed enum, including the extremes of its range.</summary>
public enum InteropEnum
{
    None = 0,
    One = 1,
    Two = 2,
    Negative = -1,
    Min = int.MinValue,
    Max = int.MaxValue,
}

/// <summary>A [Flags] enum backed by <see cref="ushort"/>.</summary>
[Flags]
public enum InteropFlags : ushort
{
    None = 0,
    A = 1,
    B = 2,
    C = 4,
    High = 0x8000,
    All = A | B | C | High,
}

/// <summary>A long-backed enum with values outside the int and 2^53 ranges.</summary>
public enum InteropLongEnum : long
{
    Zero = 0,
    Negative = -5,
    Big = 1L << 40,
    BeyondDouble = (1L << 53) + 1,
    Min = long.MinValue,
    Max = long.MaxValue,
}

/// <summary>Permissions of the interop authorization methods.</summary>
public enum InteropPermission
{
    Read = 1,
    Admin = 2,
}

/// <summary>One member of every built-in type (protocol specification §10.4), keys 0 to 31.</summary>
[NexusObject]
public class InteropAllTypes
{
    [NexusKey(0)] public bool BoolValue { get; set; }
    [NexusKey(1)] public sbyte SByteValue { get; set; }
    [NexusKey(2)] public byte ByteValue { get; set; }
    [NexusKey(3)] public short Int16Value { get; set; }
    [NexusKey(4)] public ushort UInt16Value { get; set; }
    [NexusKey(5)] public int Int32Value { get; set; }
    [NexusKey(6)] public uint UInt32Value { get; set; }
    [NexusKey(7)] public long Int64Value { get; set; }
    [NexusKey(8)] public ulong UInt64Value { get; set; }
    [NexusKey(9)] public float SingleValue { get; set; }
    [NexusKey(10)] public double DoubleValue { get; set; }
    [NexusKey(11)] public Half HalfValue { get; set; }
    [NexusKey(12)] public char CharValue { get; set; }
    [NexusKey(13)] public decimal DecimalValue { get; set; }
    [NexusKey(14)] public string? StringValue { get; set; }
    [NexusKey(15)] public byte[]? BytesValue { get; set; }
    [NexusKey(16)] public DateTime DateTimeValue { get; set; }
    [NexusKey(17)] public DateTimeOffset DateTimeOffsetValue { get; set; }
    [NexusKey(18)] public TimeSpan TimeSpanValue { get; set; }
    [NexusKey(19)] public DateOnly DateOnlyValue { get; set; }
    [NexusKey(20)] public TimeOnly TimeOnlyValue { get; set; }
    [NexusKey(21)] public Guid GuidValue { get; set; }
    [NexusKey(22)] public BigInteger BigIntegerValue { get; set; }
    [NexusKey(23)] public Uri? UriValue { get; set; }
    [NexusKey(24)] public Version? VersionValue { get; set; }
    [NexusKey(25)] public InteropEnum EnumValue { get; set; }
    [NexusKey(26)] public InteropFlags FlagsValue { get; set; }
    [NexusKey(27)] public InteropLongEnum LongEnumValue { get; set; }
    [NexusKey(28)] public int? NullableInt32Value { get; set; }
    [NexusKey(29)] public int[]? Int32ArrayValue { get; set; }
    [NexusKey(30)] public List<string>? StringListValue { get; set; }
    [NexusKey(31)] public Dictionary<string, int>? StringMapValue { get; set; }
}

/// <summary>A struct object.</summary>
[NexusObject]
public struct InteropStruct
{
    [NexusKey(0)] public int X { get; set; }
    [NexusKey(1)] public double Y { get; set; }
    [NexusKey(2)] public string? Label { get; set; }
}

/// <summary>A record class object with a positional constructor.</summary>
[NexusObject]
public record InteropRecord(
    [property: NexusKey(0)] string Name,
    [property: NexusKey(1)] int Count,
    [property: NexusKey(2)] InteropStruct Point);

/// <summary>A readonly record struct object.</summary>
[NexusObject]
public readonly record struct InteropRecordStruct(
    [property: NexusKey(0)] long Id,
    [property: NexusKey(1)] Guid Key);

/// <summary>An object with unused keys (1 and 2), which are nil on the wire (§10.2).</summary>
[NexusObject]
public class InteropSparse
{
    [NexusKey(0)] public int First { get; set; }
    [NexusKey(3)] public string? Fourth { get; set; }
}

/// <summary>The item of the interop list collection and channels.</summary>
[NexusObject]
public sealed record InteropItem(
    [property: NexusKey(0)] int Id,
    [property: NexusKey(1)] string? Name);

/// <summary>Objects, collections and unions nested in an object.</summary>
[NexusObject]
public class InteropNested
{
    [NexusKey(0)] public InteropRecord? Record { get; set; }
    [NexusKey(1)] public List<InteropItem>? Items { get; set; }
    [NexusKey(2)] public Dictionary<int, InteropStruct>? ByIndex { get; set; }
    [NexusKey(3)] public InteropShape? Shape { get; set; }
    [NexusKey(4)] public InteropNested? Child { get; set; }
}

/// <summary>A union over an abstract class (§10.3).</summary>
[NexusObject]
[NexusUnion<InteropCircle>(0)]
[NexusUnion<InteropRectangle>(1)]
public abstract class InteropShape
{
}

/// <summary>Union case 0 of <see cref="InteropShape"/>.</summary>
[NexusObject]
public class InteropCircle : InteropShape
{
    [NexusKey(0)] public double Radius { get; set; }
}

/// <summary>Union case 1 of <see cref="InteropShape"/>.</summary>
[NexusObject]
public class InteropRectangle : InteropShape
{
    [NexusKey(0)] public double Width { get; set; }
    [NexusKey(1)] public double Height { get; set; }
}

/// <summary>A union over an interface (§10.3).</summary>
[NexusObject]
[NexusUnion<InteropTextMessage>(0)]
[NexusUnion<InteropBinaryMessage>(1)]
public interface IInteropMessage
{
}

/// <summary>Union case 0 of <see cref="IInteropMessage"/>.</summary>
[NexusObject]
public class InteropTextMessage : IInteropMessage
{
    [NexusKey(0)] public string? Text { get; set; }
}

/// <summary>Union case 1 of <see cref="IInteropMessage"/>.</summary>
[NexusObject]
public class InteropBinaryMessage : IInteropMessage
{
    [NexusKey(0)] public byte[]? Data { get; set; }
    [NexusKey(1)] public InteropFlags Flags { get; set; }
}

/// <summary>A generic object with one type parameter.</summary>
[NexusObject]
public class InteropBox<T>
{
    [NexusKey(0)] public T Value { get; set; } = default!;
}

/// <summary>A generic object with two type parameters, one of them used in a collection.</summary>
[NexusObject]
public class InteropPair<TFirst, TSecond>
{
    [NexusKey(0)] public TFirst First { get; set; } = default!;
    [NexusKey(1)] public List<TSecond>? Second { get; set; }
}

/// <summary>
/// A type serialized by a user formatter (<see cref="InteropPointFormatter"/>) instead of [NexusObject]. Its Java and
/// JavaScript formatters are mapped with NexusTargetFormatter.
/// </summary>
public readonly record struct InteropPoint(int X, int Y);

/// <summary>
/// Formatter of <see cref="InteropPoint"/>: <c>[X, Y]</c>, two canonical integers. The Java and JavaScript formatters
/// write the same bytes.
/// </summary>
public sealed class InteropPointFormatter : NexusFormatter<InteropPoint>
{
    /// <inheritdoc />
    public override void Serialize(ref MsgPackWriter writer, InteropPoint value)
    {
        writer.WriteArrayHeader(2);
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    /// <inheritdoc />
    public override void Deserialize(ref MsgPackReader reader, ref InteropPoint value)
    {
        reader.ReadArrayHeader(2);
        value = new InteropPoint(reader.ReadInt32(), reader.ReadInt32());
    }
}
