using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexNet.Serialization;

/// <summary>
/// NexNet application ext type codes.
/// </summary>
public static class NexusExtType
{
    /// <summary>
    /// Array of fixed-size primitives: one kind byte followed by the elements' little-endian bytes.
    /// </summary>
    public const sbyte PrimitiveArray = 78;
}

/// <summary>
/// Element kinds of the <see cref="NexusExtType.PrimitiveArray"/> ext.
/// </summary>
public enum PrimitiveKind : byte
{
    /// <summary>Not a supported primitive.</summary>
    None = 0,
    /// <summary><see cref="sbyte"/></summary>
    SByte = 1,
    /// <summary><see cref="short"/></summary>
    Int16 = 2,
    /// <summary><see cref="ushort"/></summary>
    UInt16 = 3,
    /// <summary><see cref="int"/></summary>
    Int32 = 4,
    /// <summary><see cref="uint"/></summary>
    UInt32 = 5,
    /// <summary><see cref="long"/></summary>
    Int64 = 6,
    /// <summary><see cref="ulong"/></summary>
    UInt64 = 7,
    /// <summary><see cref="float"/></summary>
    Single = 8,
    /// <summary><see cref="double"/></summary>
    Double = 9,
    /// <summary><see cref="char"/></summary>
    Char = 10,
    /// <summary><see cref="System.Half"/></summary>
    Half = 11,
}

/// <summary>
/// Encodes spans of fixed-size primitives as a single little-endian ext blob so they can be block-copied.
/// </summary>
/// <typeparam name="T">The primitive element type.</typeparam>
public static class PrimitiveArrayCodec<T>
    where T : unmanaged
{
    /// <summary>
    /// Kind of <typeparamref name="T"/>, or <see cref="PrimitiveKind.None"/> when unsupported.
    /// </summary>
    public static readonly PrimitiveKind Kind = GetKind();

    /// <summary>
    /// True when <typeparamref name="T"/> can use the ext encoding.
    /// </summary>
    public static bool IsSupported => Kind != PrimitiveKind.None;

    private static PrimitiveKind GetKind()
    {
        if (typeof(T) == typeof(sbyte)) return PrimitiveKind.SByte;
        if (typeof(T) == typeof(short)) return PrimitiveKind.Int16;
        if (typeof(T) == typeof(ushort)) return PrimitiveKind.UInt16;
        if (typeof(T) == typeof(int)) return PrimitiveKind.Int32;
        if (typeof(T) == typeof(uint)) return PrimitiveKind.UInt32;
        if (typeof(T) == typeof(long)) return PrimitiveKind.Int64;
        if (typeof(T) == typeof(ulong)) return PrimitiveKind.UInt64;
        if (typeof(T) == typeof(float)) return PrimitiveKind.Single;
        if (typeof(T) == typeof(double)) return PrimitiveKind.Double;
        if (typeof(T) == typeof(char)) return PrimitiveKind.Char;
        if (typeof(T) == typeof(Half)) return PrimitiveKind.Half;
        return PrimitiveKind.None;
    }

    // Arrays up to this many bytes are written with a single GetSpan call.
    private const int SingleSpanLimit = 4096;

    /// <summary>
    /// Writes the elements as one ext value.
    /// </summary>
    public static void Write(ref MsgPackWriter writer, ReadOnlySpan<T> values)
    {
        var byteCount = checked(values.Length * Unsafe.SizeOf<T>());
        var bytes = MemoryMarshal.AsBytes(values);

        // Small arrays: header, kind byte and data in one span.
        if (BitConverter.IsLittleEndian && byteCount <= SingleSpanLimit)
        {
            var span = writer.GetSpan(MsgPackWriter.MaxExtHeaderSize + 1 + byteCount);
            var header = MsgPackWriter.WriteExtHeader(span, NexusExtType.PrimitiveArray, 1 + byteCount);
            span[header] = (byte)Kind;
            bytes.CopyTo(span.Slice(header + 1));
            writer.Advance(header + 1 + byteCount);
            return;
        }

        writer.WriteExtHeader(NexusExtType.PrimitiveArray, checked(1 + byteCount));
        var kindSpan = writer.GetSpan(1);
        kindSpan[0] = (byte)Kind;
        writer.Advance(1);

        if (BitConverter.IsLittleEndian)
        {
            writer.WriteRaw(bytes);
            return;
        }

        // Big-endian host: write in chunks and swap each chunk to little-endian.
        while (bytes.Length > 0)
        {
            var chunk = Math.Min(bytes.Length, 64 * 1024 / Unsafe.SizeOf<T>() * Unsafe.SizeOf<T>());
            var dest = writer.GetSpan(chunk).Slice(0, chunk);
            bytes.Slice(0, chunk).CopyTo(dest);
            SwapInPlace(dest);
            writer.Advance(chunk);
            bytes = bytes.Slice(chunk);
        }
    }

    /// <summary>
    /// Reads the ext header and kind byte and returns the element count.
    /// </summary>
    public static int ReadHeader(ref MsgPackReader reader)
    {
        var length = reader.ReadExtHeader(out var typeCode);
        if (typeCode != NexusExtType.PrimitiveArray)
            throw NexusSerializationException.InvalidExt($"unexpected ext type {typeCode}");
        if (length < 1)
            throw NexusSerializationException.InvalidExt("missing kind byte");

        var kind = reader.ReadRawByte();
        if (kind != (byte)Kind)
            throw NexusSerializationException.InvalidExt($"element kind {kind} does not match {Kind}");

        var dataLength = length - 1;
        if (dataLength % Unsafe.SizeOf<T>() != 0)
            throw NexusSerializationException.InvalidExt("data length is not a multiple of the element size");

        return dataLength / Unsafe.SizeOf<T>();
    }

    /// <summary>
    /// Reads <paramref name="destination"/>.Length elements written by <see cref="Write"/> after <see cref="ReadHeader"/>.
    /// </summary>
    public static void ReadElements(ref MsgPackReader reader, Span<T> destination)
    {
        var bytes = MemoryMarshal.AsBytes(destination);
        reader.ReadRawBytes(bytes);
        if (!BitConverter.IsLittleEndian)
            SwapInPlace(bytes);
    }

    /// <summary>
    /// Reads an ext primitive array into a new array.
    /// </summary>
    public static T[] ReadArray(ref MsgPackReader reader)
    {
        var count = ReadHeader(ref reader);
        if (count == 0)
            return Array.Empty<T>();

        var result = GC.AllocateUninitializedArray<T>(count);
        ReadElements(ref reader, result);
        return result;
    }

    /// <summary>
    /// Swaps each element of a little-endian byte span in place. Exposed for tests of the big-endian path.
    /// </summary>
    internal static void SwapInPlace(Span<byte> bytes)
    {
        switch (Unsafe.SizeOf<T>())
        {
            case 2:
                var s = MemoryMarshal.Cast<byte, ushort>(bytes);
                BinaryPrimitives.ReverseEndianness(s, s);
                break;
            case 4:
                var i = MemoryMarshal.Cast<byte, uint>(bytes);
                BinaryPrimitives.ReverseEndianness(i, i);
                break;
            case 8:
                var l = MemoryMarshal.Cast<byte, ulong>(bytes);
                BinaryPrimitives.ReverseEndianness(l, l);
                break;
        }
    }
}

/// <summary>
/// <c>T[]</c> of a fixed-size primitive, encoded as the primitive-array ext.
/// </summary>
public sealed class PrimitiveArrayFormatter<T> : NexusFormatter<T[]>
    where T : unmanaged
{
    /// <summary>Shared instance.</summary>
    public static readonly PrimitiveArrayFormatter<T> Instance = new();

    /// <inheritdoc />
    public override void Serialize(ref MsgPackWriter writer, T[]? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        PrimitiveArrayCodec<T>.Write(ref writer, value);
    }

    /// <inheritdoc />
    public override void Deserialize(ref MsgPackReader reader, ref T[]? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = PrimitiveArrayCodec<T>.ReadHeader(ref reader);
        if (value is null || value.Length != count)
            value = count == 0 ? Array.Empty<T>() : GC.AllocateUninitializedArray<T>(count);

        PrimitiveArrayCodec<T>.ReadElements(ref reader, value);
    }
}

/// <summary>
/// <c>List&lt;T&gt;</c> of a fixed-size primitive, encoded as the primitive-array ext.
/// </summary>
public sealed class PrimitiveListFormatter<T> : NexusFormatter<List<T>>
    where T : unmanaged
{
    /// <summary>Shared instance.</summary>
    public static readonly PrimitiveListFormatter<T> Instance = new();

    /// <inheritdoc />
    public override void Serialize(ref MsgPackWriter writer, List<T>? value)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        PrimitiveArrayCodec<T>.Write(ref writer, CollectionsMarshal.AsSpan(value));
    }

    /// <inheritdoc />
    public override void Deserialize(ref MsgPackReader reader, ref List<T>? value)
    {
        if (reader.TryReadNil())
        {
            value = null;
            return;
        }

        var count = PrimitiveArrayCodec<T>.ReadHeader(ref reader);
        var list = new List<T>(count);
        CollectionsMarshal.SetCount(list, count);
        PrimitiveArrayCodec<T>.ReadElements(ref reader, CollectionsMarshal.AsSpan(list));
        value = list;
    }
}

/// <summary>
/// <c>Memory&lt;T&gt;</c> of a fixed-size primitive, encoded as the primitive-array ext.
/// </summary>
public sealed class PrimitiveMemoryFormatter<T> : NexusFormatter<Memory<T>>
    where T : unmanaged
{
    /// <summary>Shared instance.</summary>
    public static readonly PrimitiveMemoryFormatter<T> Instance = new();

    /// <inheritdoc />
    public override void Serialize(ref MsgPackWriter writer, Memory<T> value)
        => PrimitiveArrayCodec<T>.Write(ref writer, value.Span);

    /// <inheritdoc />
    public override void Deserialize(ref MsgPackReader reader, ref Memory<T> value)
    {
        if (reader.TryReadNil())
        {
            value = default;
            return;
        }

        value = PrimitiveArrayCodec<T>.ReadArray(ref reader);
    }
}

/// <summary>
/// <c>ReadOnlyMemory&lt;T&gt;</c> of a fixed-size primitive, encoded as the primitive-array ext.
/// </summary>
public sealed class PrimitiveReadOnlyMemoryFormatter<T> : NexusFormatter<ReadOnlyMemory<T>>
    where T : unmanaged
{
    /// <summary>Shared instance.</summary>
    public static readonly PrimitiveReadOnlyMemoryFormatter<T> Instance = new();

    /// <inheritdoc />
    public override void Serialize(ref MsgPackWriter writer, ReadOnlyMemory<T> value)
        => PrimitiveArrayCodec<T>.Write(ref writer, value.Span);

    /// <inheritdoc />
    public override void Deserialize(ref MsgPackReader reader, ref ReadOnlyMemory<T> value)
    {
        if (reader.TryReadNil())
        {
            value = default;
            return;
        }

        value = PrimitiveArrayCodec<T>.ReadArray(ref reader);
    }
}
