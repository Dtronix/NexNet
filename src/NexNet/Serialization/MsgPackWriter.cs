using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace NexNet.Serialization;

/// <summary>
/// Writes MessagePack values into an <see cref="IBufferWriter{T}"/>.
/// Values use the smallest (canonical) encoding. Multi-byte numbers are big-endian as required by the spec.
/// </summary>
/// <remarks>
/// The writer caches the span obtained from the output and only calls <see cref="IBufferWriter{T}.Advance"/>
/// when the span runs out or on <see cref="Flush"/>. Every code path that writes must end with <see cref="Flush"/>.
/// Pass the writer by <c>ref</c>.
/// </remarks>
public ref struct MsgPackWriter
{
    private const int MinimumBufferSize = 512;

    // Strings above this many chars are measured exactly instead of reserving the worst case (3 bytes per char).
    private const int LargeStringThreshold = 64 * 1024;

    private readonly IBufferWriter<byte> _output;
    private Span<byte> _span;
    private int _buffered;

    /// <summary>
    /// Benchmark-only mode: integers are always written in the full-width form of their .NET type.
    /// The output is still valid MessagePack.
    /// </summary>
    internal bool FixedWidth;

    /// <summary>
    /// Creates a writer over the specified output.
    /// </summary>
    /// <param name="output">Destination buffer writer.</param>
    public MsgPackWriter(IBufferWriter<byte> output)
    {
        _output = output;
        _span = default;
        _buffered = 0;
        FixedWidth = false;
    }

    /// <summary>
    /// Commits all buffered bytes to the output.
    /// </summary>
    public void Flush()
    {
        if (_buffered > 0)
        {
            _output.Advance(_buffered);
            _buffered = 0;
        }

        _span = default;
    }

    /// <summary>
    /// Gets a span of at least <paramref name="sizeHint"/> bytes to write raw data into.
    /// Call <see cref="Advance"/> with the number of bytes written.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint)
    {
        if (_span.Length - _buffered < sizeHint)
            Refill(sizeHint);

        return _span.Slice(_buffered);
    }

    /// <summary>
    /// Marks <paramref name="count"/> bytes of the span from <see cref="GetSpan"/> as written.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        _buffered += count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill(int sizeHint)
    {
        Flush();
        _span = _output.GetSpan(Math.Max(sizeHint, MinimumBufferSize));
    }

    // ---------------------------------------------------------------- Nil / Bool

    /// <summary>
    /// Writes nil.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNil()
    {
        GetSpan(1)[0] = MsgPackCode.Nil;
        _buffered += 1;
    }

    /// <summary>
    /// Writes a boolean.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(bool value)
    {
        GetSpan(1)[0] = value ? MsgPackCode.True : MsgPackCode.False;
        _buffered += 1;
    }

    // ---------------------------------------------------------------- Unsigned integers

    /// <summary>
    /// Writes an unsigned 8-bit integer.
    /// </summary>
    public void Write(byte value)
    {
        if (FixedWidth)
        {
            WriteUInt8Forced(value);
            return;
        }

        if (value <= MsgPackCode.MaxFixPositive)
        {
            GetSpan(1)[0] = value;
            _buffered += 1;
        }
        else
        {
            WriteUInt8Forced(value);
        }
    }

    /// <summary>
    /// Writes an unsigned 16-bit integer.
    /// </summary>
    public void Write(ushort value)
    {
        if (FixedWidth)
        {
            WriteUInt16Forced(value);
            return;
        }

        WriteUInt32Compact(value);
    }

    /// <summary>
    /// Writes an unsigned 32-bit integer.
    /// </summary>
    public void Write(uint value)
    {
        if (FixedWidth)
        {
            WriteUInt32Forced(value);
            return;
        }

        WriteUInt32Compact(value);
    }

    /// <summary>
    /// Writes an unsigned 64-bit integer.
    /// </summary>
    public void Write(ulong value)
    {
        if (FixedWidth)
        {
            WriteUInt64Forced(value);
            return;
        }

        if (value <= uint.MaxValue)
            WriteUInt32Compact((uint)value);
        else
            WriteUInt64Forced(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt32Compact(uint value)
    {
        Span<byte> s;
        if (value <= MsgPackCode.MaxFixPositive)
        {
            s = GetSpan(1);
            s[0] = (byte)value;
            _buffered += 1;
        }
        else if (value <= byte.MaxValue)
        {
            s = GetSpan(2);
            s[0] = MsgPackCode.UInt8;
            s[1] = (byte)value;
            _buffered += 2;
        }
        else if (value <= ushort.MaxValue)
        {
            s = GetSpan(3);
            s[0] = MsgPackCode.UInt16;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)value);
            _buffered += 3;
        }
        else
        {
            s = GetSpan(5);
            s[0] = MsgPackCode.UInt32;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), value);
            _buffered += 5;
        }
    }

    // ---------------------------------------------------------------- Signed integers

    /// <summary>
    /// Writes a signed 8-bit integer.
    /// </summary>
    public void Write(sbyte value)
    {
        if (FixedWidth)
        {
            WriteInt8Forced(value);
            return;
        }

        WriteInt32Compact(value);
    }

    /// <summary>
    /// Writes a signed 16-bit integer.
    /// </summary>
    public void Write(short value)
    {
        if (FixedWidth)
        {
            WriteInt16Forced(value);
            return;
        }

        WriteInt32Compact(value);
    }

    /// <summary>
    /// Writes a signed 32-bit integer.
    /// </summary>
    public void Write(int value)
    {
        if (FixedWidth)
        {
            WriteInt32Forced(value);
            return;
        }

        WriteInt32Compact(value);
    }

    /// <summary>
    /// Writes a signed 64-bit integer.
    /// </summary>
    public void Write(long value)
    {
        if (FixedWidth)
        {
            WriteInt64Forced(value);
            return;
        }

        if (value >= 0)
        {
            if (value <= uint.MaxValue)
                WriteUInt32Compact((uint)value);
            else
                WriteUInt64Forced((ulong)value);
        }
        else if (value >= int.MinValue)
        {
            WriteInt32Compact((int)value);
        }
        else
        {
            WriteInt64Forced(value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteInt32Compact(int value)
    {
        // Positive values use the unsigned family, matching canonical MessagePack writers.
        if (value >= 0)
        {
            WriteUInt32Compact((uint)value);
            return;
        }

        Span<byte> s;
        if (value >= -32)
        {
            s = GetSpan(1);
            s[0] = unchecked((byte)value); // negative fixint 0xe0..0xff
            _buffered += 1;
        }
        else if (value >= sbyte.MinValue)
        {
            s = GetSpan(2);
            s[0] = MsgPackCode.Int8;
            s[1] = unchecked((byte)value);
            _buffered += 2;
        }
        else if (value >= short.MinValue)
        {
            s = GetSpan(3);
            s[0] = MsgPackCode.Int16;
            BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), (short)value);
            _buffered += 3;
        }
        else
        {
            s = GetSpan(5);
            s[0] = MsgPackCode.Int32;
            BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), value);
            _buffered += 5;
        }
    }

    /// <summary>
    /// Writes a UTF-16 code unit as an unsigned integer.
    /// </summary>
    public void Write(char value) => Write((ushort)value);

    // ---------------------------------------------------------------- Forced (fixed-width) integers

    /// <summary>Writes <c>uint8</c> (0xcc) regardless of value.</summary>
    public void WriteUInt8Forced(byte value)
    {
        var s = GetSpan(2);
        s[0] = MsgPackCode.UInt8;
        s[1] = value;
        _buffered += 2;
    }

    /// <summary>Writes <c>uint16</c> (0xcd) regardless of value.</summary>
    public void WriteUInt16Forced(ushort value)
    {
        var s = GetSpan(3);
        s[0] = MsgPackCode.UInt16;
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), value);
        _buffered += 3;
    }

    /// <summary>Writes <c>uint32</c> (0xce) regardless of value.</summary>
    public void WriteUInt32Forced(uint value)
    {
        var s = GetSpan(5);
        s[0] = MsgPackCode.UInt32;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), value);
        _buffered += 5;
    }

    /// <summary>Writes <c>uint64</c> (0xcf) regardless of value.</summary>
    public void WriteUInt64Forced(ulong value)
    {
        var s = GetSpan(9);
        s[0] = MsgPackCode.UInt64;
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(1), value);
        _buffered += 9;
    }

    /// <summary>Writes <c>int8</c> (0xd0) regardless of value.</summary>
    public void WriteInt8Forced(sbyte value)
    {
        var s = GetSpan(2);
        s[0] = MsgPackCode.Int8;
        s[1] = unchecked((byte)value);
        _buffered += 2;
    }

    /// <summary>Writes <c>int16</c> (0xd1) regardless of value.</summary>
    public void WriteInt16Forced(short value)
    {
        var s = GetSpan(3);
        s[0] = MsgPackCode.Int16;
        BinaryPrimitives.WriteInt16BigEndian(s.Slice(1), value);
        _buffered += 3;
    }

    /// <summary>Writes <c>int32</c> (0xd2) regardless of value.</summary>
    public void WriteInt32Forced(int value)
    {
        var s = GetSpan(5);
        s[0] = MsgPackCode.Int32;
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(1), value);
        _buffered += 5;
    }

    /// <summary>Writes <c>int64</c> (0xd3) regardless of value.</summary>
    public void WriteInt64Forced(long value)
    {
        var s = GetSpan(9);
        s[0] = MsgPackCode.Int64;
        BinaryPrimitives.WriteInt64BigEndian(s.Slice(1), value);
        _buffered += 9;
    }

    // ---------------------------------------------------------------- Floating point

    /// <summary>
    /// Writes a 32-bit float (always <c>float32</c>).
    /// </summary>
    public void Write(float value)
    {
        var s = GetSpan(5);
        s[0] = MsgPackCode.Float32;
        BinaryPrimitives.WriteSingleBigEndian(s.Slice(1), value);
        _buffered += 5;
    }

    /// <summary>
    /// Writes a 64-bit float (always <c>float64</c>).
    /// </summary>
    public void Write(double value)
    {
        var s = GetSpan(9);
        s[0] = MsgPackCode.Float64;
        BinaryPrimitives.WriteDoubleBigEndian(s.Slice(1), value);
        _buffered += 9;
    }

    // ---------------------------------------------------------------- Containers

    /// <summary>
    /// Writes an array header for <paramref name="count"/> elements.
    /// </summary>
    public void WriteArrayHeader(int count)
    {
        WriteCollectionHeader((uint)count, MsgPackCode.MinFixArray, MsgPackCode.Array16, MsgPackCode.Array32);
    }

    /// <summary>
    /// Writes a map header for <paramref name="count"/> key/value pairs.
    /// </summary>
    public void WriteMapHeader(int count)
    {
        WriteCollectionHeader((uint)count, MsgPackCode.MinFixMap, MsgPackCode.Map16, MsgPackCode.Map32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteCollectionHeader(uint count, byte fixBase, byte code16, byte code32)
    {
        Span<byte> s;
        if (count <= MsgPackCode.MaxFixCollectionCount)
        {
            s = GetSpan(1);
            s[0] = (byte)(fixBase | count);
            _buffered += 1;
        }
        else if (count <= ushort.MaxValue)
        {
            s = GetSpan(3);
            s[0] = code16;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)count);
            _buffered += 3;
        }
        else
        {
            s = GetSpan(5);
            s[0] = code32;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), count);
            _buffered += 5;
        }
    }

    // ---------------------------------------------------------------- Strings

    /// <summary>
    /// Writes a string as UTF-8, or nil when <paramref name="value"/> is null.
    /// </summary>
    public void Write(string? value)
    {
        if (value is null)
        {
            WriteNil();
            return;
        }

        WriteString(value.AsSpan());
    }

    /// <summary>
    /// Writes UTF-16 characters as a UTF-8 MessagePack string.
    /// </summary>
    /// <remarks>
    /// The header needs the UTF-8 byte length, which is only known after encoding. The writer reserves room for the
    /// largest header the string could need, encodes once directly into the buffer and moves the bytes back when a
    /// smaller header suffices. The result is always the canonical (smallest) header.
    /// </remarks>
    public void WriteString(ReadOnlySpan<char> chars)
    {
        int written;
        if (chars.Length > LargeStringThreshold)
        {
            // Avoid reserving 3x the size for large strings: measure exactly, then encode once.
            var byteCount = Encoding.UTF8.GetByteCount(chars);
            WriteStringHeader(byteCount);
            var dest = GetSpan(byteCount);
            written = EncodeUtf8(chars, dest);
            _buffered += written;
            return;
        }

        var max = Encoding.UTF8.GetMaxByteCount(chars.Length);
        if (max <= MsgPackCode.MaxFixStrLength)
        {
            var s = GetSpan(1 + max);
            written = EncodeUtf8(chars, s.Slice(1));
            s[0] = (byte)(MsgPackCode.MinFixStr | written);
            _buffered += 1 + written;
            return;
        }

        var reservedHeader = max <= byte.MaxValue ? 2 : max <= ushort.MaxValue ? 3 : 5;
        var span = GetSpan(reservedHeader + max);
        written = EncodeUtf8(chars, span.Slice(reservedHeader));

        var needed = StringHeaderSize(written);
        if (needed < reservedHeader)
            span.Slice(reservedHeader, written).CopyTo(span.Slice(needed));

        WriteStringHeaderTo(span, needed, written);
        _buffered += needed + written;
    }

    /// <summary>
    /// Writes a pre-encoded UTF-8 string.
    /// </summary>
    public void WriteStringUtf8(ReadOnlySpan<byte> utf8)
    {
        WriteStringHeader(utf8.Length);
        WriteRaw(utf8);
    }

    /// <summary>
    /// Writes a string header for <paramref name="byteLength"/> UTF-8 bytes.
    /// </summary>
    public void WriteStringHeader(int byteLength)
    {
        var size = StringHeaderSize(byteLength);
        var s = GetSpan(size);
        WriteStringHeaderTo(s, size, byteLength);
        _buffered += size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StringHeaderSize(int byteLength)
    {
        return byteLength <= MsgPackCode.MaxFixStrLength ? 1
            : byteLength <= byte.MaxValue ? 2
            : byteLength <= ushort.MaxValue ? 3
            : 5;
    }

    private static void WriteStringHeaderTo(Span<byte> span, int headerSize, int byteLength)
    {
        switch (headerSize)
        {
            case 1:
                span[0] = (byte)(MsgPackCode.MinFixStr | byteLength);
                break;
            case 2:
                span[0] = MsgPackCode.Str8;
                span[1] = (byte)byteLength;
                break;
            case 3:
                span[0] = MsgPackCode.Str16;
                BinaryPrimitives.WriteUInt16BigEndian(span.Slice(1), (ushort)byteLength);
                break;
            default:
                span[0] = MsgPackCode.Str32;
                BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1), (uint)byteLength);
                break;
        }
    }

    private static int EncodeUtf8(ReadOnlySpan<char> chars, Span<byte> destination)
    {
        // Lone surrogates become U+FFFD, matching the default .NET UTF-8 encoder.
        Utf8.FromUtf16(chars, destination, out _, out var written, replaceInvalidSequences: true);
        return written;
    }

    // ---------------------------------------------------------------- Binary

    /// <summary>
    /// Writes a bin header for <paramref name="length"/> bytes.
    /// </summary>
    public void WriteBinaryHeader(int length)
    {
        Span<byte> s;
        if (length <= byte.MaxValue)
        {
            s = GetSpan(2);
            s[0] = MsgPackCode.Bin8;
            s[1] = (byte)length;
            _buffered += 2;
        }
        else if (length <= ushort.MaxValue)
        {
            s = GetSpan(3);
            s[0] = MsgPackCode.Bin16;
            BinaryPrimitives.WriteUInt16BigEndian(s.Slice(1), (ushort)length);
            _buffered += 3;
        }
        else
        {
            s = GetSpan(5);
            s[0] = MsgPackCode.Bin32;
            BinaryPrimitives.WriteUInt32BigEndian(s.Slice(1), (uint)length);
            _buffered += 5;
        }
    }

    /// <summary>
    /// Writes binary data.
    /// </summary>
    public void WriteBinary(ReadOnlySpan<byte> data)
    {
        WriteBinaryHeader(data.Length);
        WriteRaw(data);
    }

    /// <summary>
    /// Writes binary data.
    /// </summary>
    public void WriteBinary(in ReadOnlySequence<byte> data)
    {
        WriteBinaryHeader(checked((int)data.Length));
        WriteRaw(data);
    }

    // ---------------------------------------------------------------- Ext

    /// <summary>
    /// Writes an ext header with the smallest form for <paramref name="length"/> data bytes.
    /// </summary>
    public void WriteExtHeader(sbyte typeCode, int length)
    {
        _buffered += WriteExtHeader(GetSpan(MaxExtHeaderSize), typeCode, length);
    }

    /// <summary>
    /// Largest ext header (ext32: code, 4-byte length, type).
    /// </summary>
    internal const int MaxExtHeaderSize = 6;

    /// <summary>
    /// Writes the smallest ext header for <paramref name="length"/> data bytes into <paramref name="destination"/>
    /// (at least <see cref="MaxExtHeaderSize"/> bytes) and returns the header size.
    /// </summary>
    internal static int WriteExtHeader(Span<byte> destination, sbyte typeCode, int length)
    {
        byte fixCode = length switch
        {
            1 => MsgPackCode.FixExt1,
            2 => MsgPackCode.FixExt2,
            4 => MsgPackCode.FixExt4,
            8 => MsgPackCode.FixExt8,
            16 => MsgPackCode.FixExt16,
            _ => 0
        };

        if (fixCode != 0)
        {
            destination[1] = unchecked((byte)typeCode);
            destination[0] = fixCode;
            return 2;
        }

        if (length <= byte.MaxValue)
        {
            destination[2] = unchecked((byte)typeCode);
            destination[0] = MsgPackCode.Ext8;
            destination[1] = (byte)length;
            return 3;
        }

        if (length <= ushort.MaxValue)
        {
            destination[3] = unchecked((byte)typeCode);
            destination[0] = MsgPackCode.Ext16;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(1), (ushort)length);
            return 4;
        }

        destination[5] = unchecked((byte)typeCode);
        destination[0] = MsgPackCode.Ext32;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(1), (uint)length);
        return 6;
    }

    // ---------------------------------------------------------------- Raw

    /// <summary>
    /// Copies raw bytes into the output without any header. The bytes must already be valid MessagePack
    /// where a value is expected.
    /// </summary>
    public void WriteRaw(ReadOnlySpan<byte> data)
    {
        // Large data is copied in chunks so no single huge span is requested.
        while (data.Length > 0)
        {
            var dest = GetSpan(Math.Min(data.Length, 64 * 1024));
            var count = Math.Min(dest.Length - 0, data.Length);
            data.Slice(0, count).CopyTo(dest);
            _buffered += count;
            data = data.Slice(count);
        }
    }

    /// <summary>
    /// Copies raw bytes into the output without any header.
    /// </summary>
    public void WriteRaw(in ReadOnlySequence<byte> data)
    {
        foreach (var segment in data)
            WriteRaw(segment.Span);
    }
}
