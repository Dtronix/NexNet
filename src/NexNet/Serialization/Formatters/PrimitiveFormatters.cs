using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace NexNet.Serialization.Formatters;

#pragma warning disable CS1591 // Formatter members are self-describing.

public sealed class BooleanFormatter : NexusFormatter<bool>
{
    public static readonly BooleanFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, bool value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref bool value) => value = reader.ReadBoolean();
}

public sealed class ByteFormatter : NexusFormatter<byte>
{
    public static readonly ByteFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, byte value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref byte value) => value = reader.ReadByte();
}

public sealed class SByteFormatter : NexusFormatter<sbyte>
{
    public static readonly SByteFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, sbyte value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref sbyte value) => value = reader.ReadSByte();
}

public sealed class Int16Formatter : NexusFormatter<short>
{
    public static readonly Int16Formatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, short value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref short value) => value = reader.ReadInt16();
}

public sealed class UInt16Formatter : NexusFormatter<ushort>
{
    public static readonly UInt16Formatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, ushort value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref ushort value) => value = reader.ReadUInt16();
}

public sealed class Int32Formatter : NexusFormatter<int>
{
    public static readonly Int32Formatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, int value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref int value) => value = reader.ReadInt32();
}

public sealed class UInt32Formatter : NexusFormatter<uint>
{
    public static readonly UInt32Formatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, uint value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref uint value) => value = reader.ReadUInt32();
}

public sealed class Int64Formatter : NexusFormatter<long>
{
    public static readonly Int64Formatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, long value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref long value) => value = reader.ReadInt64();
}

public sealed class UInt64Formatter : NexusFormatter<ulong>
{
    public static readonly UInt64Formatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, ulong value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref ulong value) => value = reader.ReadUInt64();
}

public sealed class SingleFormatter : NexusFormatter<float>
{
    public static readonly SingleFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, float value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref float value) => value = reader.ReadSingle();
}

public sealed class DoubleFormatter : NexusFormatter<double>
{
    public static readonly DoubleFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, double value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref double value) => value = reader.ReadDouble();
}

public sealed class HalfFormatter : NexusFormatter<Half>
{
    public static readonly HalfFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, Half value) => writer.Write((float)value);
    public override void Deserialize(ref MsgPackReader reader, ref Half value) => value = (Half)reader.ReadSingle();
}

public sealed class CharFormatter : NexusFormatter<char>
{
    public static readonly CharFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, char value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref char value) => value = reader.ReadChar();
}

public sealed class StringFormatter : NexusFormatter<string>
{
    public static readonly StringFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, string? value) => writer.Write(value);
    public override void Deserialize(ref MsgPackReader reader, ref string? value) => value = reader.ReadString();
}

// ------------------------------------------------------------------ Binary

public sealed class ByteArrayFormatter : NexusFormatter<byte[]>
{
    public static readonly ByteArrayFormatter Instance = new();

    public override void Serialize(ref MsgPackWriter writer, byte[]? value)
    {
        if (value is null)
            writer.WriteNil();
        else
            writer.WriteBinary(value);
    }

    public override void Deserialize(ref MsgPackReader reader, ref byte[]? value) => value = reader.ReadBinaryToArray();
}

public sealed class MemoryByteFormatter : NexusFormatter<Memory<byte>>
{
    public static readonly MemoryByteFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, Memory<byte> value) => writer.WriteBinary(value.Span);
    public override void Deserialize(ref MsgPackReader reader, ref Memory<byte> value) => value = reader.ReadBinaryToArray();
}

public sealed class ReadOnlyMemoryByteFormatter : NexusFormatter<ReadOnlyMemory<byte>>
{
    public static readonly ReadOnlyMemoryByteFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, ReadOnlyMemory<byte> value) => writer.WriteBinary(value.Span);
    public override void Deserialize(ref MsgPackReader reader, ref ReadOnlyMemory<byte> value) => value = reader.ReadBinaryToArray();
}

public sealed class ArraySegmentByteFormatter : NexusFormatter<ArraySegment<byte>>
{
    public static readonly ArraySegmentByteFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, ArraySegment<byte> value) => writer.WriteBinary(value.AsSpan());

    public override void Deserialize(ref MsgPackReader reader, ref ArraySegment<byte> value)
    {
        var array = reader.ReadBinaryToArray();
        value = array is null ? default : new ArraySegment<byte>(array);
    }
}

public sealed class ReadOnlySequenceByteFormatter : NexusFormatter<ReadOnlySequence<byte>>
{
    public static readonly ReadOnlySequenceByteFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, ReadOnlySequence<byte> value) => writer.WriteBinary(value);

    public override void Deserialize(ref MsgPackReader reader, ref ReadOnlySequence<byte> value)
    {
        // Copy: the source buffer is not owned by the deserialized value.
        var array = reader.ReadBinaryToArray();
        value = array is null ? default : new ReadOnlySequence<byte>(array);
    }
}

// ------------------------------------------------------------------ Date / time

/// <summary>
/// <see cref="DateTime"/> as int64 <see cref="DateTime.ToBinary"/>, preserving <see cref="DateTime.Kind"/>.
/// </summary>
public sealed class DateTimeFormatter : NexusFormatter<DateTime>
{
    public static readonly DateTimeFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, DateTime value) => writer.Write(value.ToBinary());

    // DateTime.ToBinary layout: kind in the top two bits (0 Unspecified, 1 Utc, 2/3 Local), ticks in the rest.
    private const long LocalKindMask = unchecked((long)0x8000000000000000);
    private const long TicksMask = 0x3FFFFFFFFFFFFFFF;

    public override void Deserialize(ref MsgPackReader reader, ref DateTime value)
    {
        var binary = reader.ReadInt64();

        // Utc and Unspecified are decoded directly (same result as FromBinary). Local values need the time zone
        // conversion in FromBinary.
        var ticks = binary & TicksMask;
        if ((binary & LocalKindMask) == 0 && (ulong)ticks <= (ulong)DateTime.MaxValue.Ticks)
        {
            value = new DateTime(ticks, (DateTimeKind)((ulong)binary >> 62));
            return;
        }

        value = FromBinarySlow(binary);
    }

    private static DateTime FromBinarySlow(long binary)
    {
        try
        {
            return DateTime.FromBinary(binary);
        }
        catch (ArgumentException e)
        {
            throw new NexusSerializationException("Invalid DateTime value.", e);
        }
    }
}

/// <summary>
/// <see cref="DateTimeOffset"/> as <c>[UtcTicks (int64), offset minutes (int16)]</c>.
/// </summary>
public sealed class DateTimeOffsetFormatter : NexusFormatter<DateTimeOffset>
{
    public static readonly DateTimeOffsetFormatter Instance = new();

    public override void Serialize(ref MsgPackWriter writer, DateTimeOffset value)
    {
        writer.WriteArrayHeader(2);
        writer.Write(value.UtcTicks);
        writer.Write((short)value.TotalOffsetMinutes);
    }

    public override void Deserialize(ref MsgPackReader reader, ref DateTimeOffset value)
    {
        reader.ReadArrayHeader(2);
        var utcTicks = reader.ReadInt64();
        var offsetMinutes = reader.ReadInt16();
        try
        {
            var offset = TimeSpan.FromMinutes(offsetMinutes);
            value = new DateTimeOffset(utcTicks + offset.Ticks, offset);
        }
        catch (ArgumentException e)
        {
            throw new NexusSerializationException("Invalid DateTimeOffset value.", e);
        }
    }
}

public sealed class TimeSpanFormatter : NexusFormatter<TimeSpan>
{
    public static readonly TimeSpanFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, TimeSpan value) => writer.Write(value.Ticks);
    public override void Deserialize(ref MsgPackReader reader, ref TimeSpan value) => value = new TimeSpan(reader.ReadInt64());
}

public sealed class DateOnlyFormatter : NexusFormatter<DateOnly>
{
    public static readonly DateOnlyFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, DateOnly value) => writer.Write(value.DayNumber);

    public override void Deserialize(ref MsgPackReader reader, ref DateOnly value)
    {
        try
        {
            value = DateOnly.FromDayNumber(reader.ReadInt32());
        }
        catch (ArgumentException e)
        {
            throw new NexusSerializationException("Invalid DateOnly value.", e);
        }
    }
}

public sealed class TimeOnlyFormatter : NexusFormatter<TimeOnly>
{
    public static readonly TimeOnlyFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, TimeOnly value) => writer.Write(value.Ticks);

    public override void Deserialize(ref MsgPackReader reader, ref TimeOnly value)
    {
        try
        {
            value = new TimeOnly(reader.ReadInt64());
        }
        catch (ArgumentException e)
        {
            throw new NexusSerializationException("Invalid TimeOnly value.", e);
        }
    }
}

// ------------------------------------------------------------------ Other BCL types

/// <summary>
/// <see cref="Guid"/> as bin16 in RFC 4122 (big-endian) byte order.
/// </summary>
public sealed class GuidFormatter : NexusFormatter<Guid>
{
    public static readonly GuidFormatter Instance = new();

    public override void Serialize(ref MsgPackWriter writer, Guid value)
    {
        writer.WriteBinaryHeader(16);
        var span = writer.GetSpan(16);
        value.TryWriteBytes(span, bigEndian: true, out _);
        writer.Advance(16);
    }

    public override void Deserialize(ref MsgPackReader reader, ref Guid value)
    {
        var length = reader.ReadBinaryHeader();
        if (length != 16)
            throw new NexusSerializationException("Guid must be 16 bytes.");

        Span<byte> bytes = stackalloc byte[16];
        reader.ReadRawBytes(bytes);
        value = new Guid(bytes, bigEndian: true);
    }
}

/// <summary>
/// <see cref="decimal"/> as bin16: <see cref="decimal.GetBits(decimal)"/> as four little-endian int32 values (lo, mid, hi, flags).
/// </summary>
public sealed class DecimalFormatter : NexusFormatter<decimal>
{
    public static readonly DecimalFormatter Instance = new();

    public override void Serialize(ref MsgPackWriter writer, decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        writer.WriteBinaryHeader(16);
        var span = writer.GetSpan(16);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(i * 4), bits[i]);
        writer.Advance(16);
    }

    public override void Deserialize(ref MsgPackReader reader, ref decimal value)
    {
        var length = reader.ReadBinaryHeader();
        if (length != 16)
            throw new NexusSerializationException("Decimal must be 16 bytes.");

        Span<byte> bytes = stackalloc byte[16];
        reader.ReadRawBytes(bytes);
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4));

        try
        {
            value = new decimal(bits);
        }
        catch (ArgumentException e)
        {
            throw new NexusSerializationException("Invalid decimal value.", e);
        }
    }
}

public sealed class UriFormatter : NexusFormatter<Uri>
{
    public static readonly UriFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, Uri? value) => writer.Write(value?.OriginalString);

    public override void Deserialize(ref MsgPackReader reader, ref Uri? value)
    {
        var s = reader.ReadString();
        if (s is null)
        {
            value = null;
            return;
        }

        if (!Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out var uri))
            throw new NexusSerializationException("Invalid Uri value.");
        value = uri;
    }
}

public sealed class VersionFormatter : NexusFormatter<Version>
{
    public static readonly VersionFormatter Instance = new();
    public override void Serialize(ref MsgPackWriter writer, Version? value) => writer.Write(value?.ToString());

    public override void Deserialize(ref MsgPackReader reader, ref Version? value)
    {
        var s = reader.ReadString();
        if (s is null)
        {
            value = null;
            return;
        }

        if (!Version.TryParse(s, out var version))
            throw new NexusSerializationException("Invalid Version value.");
        value = version;
    }
}

/// <summary>
/// <see cref="BigInteger"/> as bin, little-endian two's complement.
/// </summary>
public sealed class BigIntegerFormatter : NexusFormatter<BigInteger>
{
    public static readonly BigIntegerFormatter Instance = new();

    public override void Serialize(ref MsgPackWriter writer, BigInteger value)
    {
        var count = value.GetByteCount();
        writer.WriteBinaryHeader(count);
        var span = writer.GetSpan(count);
        value.TryWriteBytes(span, out var written, isUnsigned: false, isBigEndian: false);
        writer.Advance(written);
    }

    public override void Deserialize(ref MsgPackReader reader, ref BigInteger value)
    {
        var length = reader.ReadBinaryHeader();
        if (length < 0)
            throw new NexusSerializationException("BigInteger cannot be nil.");

        var bytes = reader.ReadRawBytes(length);
        value = bytes.IsSingleSegment
            ? new BigInteger(bytes.FirstSpan, isUnsigned: false, isBigEndian: false)
            : new BigInteger(bytes.ToArray(), isUnsigned: false, isBigEndian: false);
    }
}
