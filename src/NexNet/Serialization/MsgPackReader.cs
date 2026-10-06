using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace NexNet.Serialization;

/// <summary>
/// Reads MessagePack values from a <see cref="ReadOnlySequence{T}"/>.
/// </summary>
/// <remarks>
/// Strict reads (<c>Read*</c>) expect a region already known to be complete and throw
/// <see cref="NexusSerializationException"/> on truncated or malformed data. <see cref="TrySkip"/> probes for
/// completeness and returns false instead. Copying the reader (<c>var probe = reader;</c>) takes a cheap snapshot.
/// Pass the reader by <c>ref</c>.
/// </remarks>
public ref struct MsgPackReader
{
    // Strings up to this many bytes are transcoded through a stack buffer; longer ones through a pooled one.
    private const int StackDecodeLimit = 256;

    // Cursor: the current segment as a span plus an index into it. Hot reads only touch _span/_index; crossing
    // into the next segment is handled by out-of-line slow paths. Reaching the end of a segment does not move to
    // the next one until a read needs more bytes, so _index may equal _span.Length.
    private readonly ReadOnlySequence<byte> _sequence;
    private ReadOnlySpan<byte> _span;
    private int _index;
    private long _segmentStart;                 // bytes of the sequence before _span[0]
    private SequencePosition _segmentPosition;  // position of _span[0]
    private SequencePosition _nextSegment;      // where the next segment is fetched from; default when none
    private readonly long _length;
    private readonly NexusSerializerOptions _options;
    private int _depth;

    /// <summary>
    /// Creates a reader over the specified sequence.
    /// </summary>
    public MsgPackReader(ReadOnlySequence<byte> source, NexusSerializerOptions? options = null)
    {
        _sequence = source;
        _length = source.Length;
        _options = options ?? NexusSerializerOptions.Untrusted;
        _depth = 0;
        _index = 0;
        _segmentStart = 0;
        _segmentPosition = source.Start;
        if (source.IsSingleSegment)
        {
            _span = source.FirstSpan;
            _nextSegment = default;
        }
        else
        {
            _nextSegment = source.Start;
            source.TryGet(ref _nextSegment, out var first);
            _span = first.Span;
        }
    }

    /// <summary>
    /// Creates a reader over the specified memory.
    /// </summary>
    public MsgPackReader(ReadOnlyMemory<byte> source, NexusSerializerOptions? options = null)
    {
        // Array-backed memory (the common case) avoids the out-of-line ReadOnlySequence(ReadOnlyMemory) constructor
        // and a second type check for .Span.
        if (MemoryMarshal.TryGetArray(source, out var segment))
        {
            _sequence = new ReadOnlySequence<byte>(segment.Array!, segment.Offset, segment.Count);
            _span = new ReadOnlySpan<byte>(segment.Array, segment.Offset, segment.Count);
        }
        else
        {
            _sequence = new ReadOnlySequence<byte>(source);
            _span = source.Span;
        }

        _length = source.Length;
        _options = options ?? NexusSerializerOptions.Untrusted;
        _depth = 0;
        _index = 0;
        _segmentStart = 0;
        _segmentPosition = _sequence.Start;
        _nextSegment = default;
    }

    /// <summary>
    /// Options in effect for this reader.
    /// </summary>
    public readonly NexusSerializerOptions Options => _options;

    /// <summary>
    /// Number of bytes consumed.
    /// </summary>
    public readonly long Consumed => ConsumedCore;

    /// <summary>
    /// Number of bytes remaining.
    /// </summary>
    public readonly long Remaining => RemainingCore;

    /// <summary>
    /// True when no bytes remain.
    /// </summary>
    public readonly bool End => EndCore;

    /// <summary>
    /// Current position in the underlying sequence.
    /// </summary>
    public readonly SequencePosition Position => PositionCore;

    /// <summary>
    /// The underlying sequence.
    /// </summary>
    public readonly ReadOnlySequence<byte> Sequence => _sequence;

    // ---------------------------------------------------------------- Cursor

    private readonly long ConsumedCore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _segmentStart + _index;
    }

    private readonly long RemainingCore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length - (_segmentStart + _index);
    }

    private readonly bool EndCore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _segmentStart + _index >= _length;
    }

    private readonly SequencePosition PositionCore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _index == 0 ? _segmentPosition : _sequence.GetPosition(_index, _segmentPosition);
    }

    private readonly ReadOnlySpan<byte> UnreadSpanCore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _span.Slice(_index);
    }

    /// <summary>
    /// Moves to the next non-empty segment. Only valid when the current span is exhausted.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool MoveNextSegment()
    {
        while (_nextSegment.GetObject() is not null)
        {
            var position = _nextSegment;
            if (!_sequence.TryGet(ref _nextSegment, out var memory))
                return false;

            _segmentStart += _span.Length;
            _segmentPosition = position;
            _span = memory.Span;
            _index = 0;
            if (_span.Length > 0)
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadByteCore(out byte value)
    {
        var index = _index;
        var span = _span;
        if ((uint)index < (uint)span.Length)
        {
            value = span[index];
            _index = index + 1;
            return true;
        }

        return TryReadByteSlow(out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadByteSlow(out byte value)
    {
        if (!MoveNextSegment())
        {
            value = 0;
            return false;
        }

        value = _span[0];
        _index = 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool TryPeekByteCore(out byte value)
    {
        var index = _index;
        var span = _span;
        if ((uint)index < (uint)span.Length)
        {
            value = span[index];
            return true;
        }

        return TryPeekByteSlow(out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly bool TryPeekByteSlow(out byte value)
    {
        var copy = this;
        return copy.TryReadByteSlow(out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceCore(long count)
    {
        if ((ulong)count <= (ulong)(_span.Length - _index))
        {
            _index += (int)count;
            return;
        }

        AdvanceSlow(count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AdvanceSlow(long count)
    {
        if (count < 0 || count > RemainingCore)
            throw NexusSerializationException.Truncated();

        while (true)
        {
            var available = _span.Length - _index;
            if (count <= available)
            {
                _index += (int)count;
                return;
            }

            count -= available;
            _index = _span.Length;
            if (!MoveNextSegment())
                throw NexusSerializationException.Truncated();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBigEndianCore(out short value)
    {
        if (_span.Length - _index >= sizeof(short))
        {
            value = BinaryPrimitives.ReadInt16BigEndian(_span.Slice(_index));
            _index += sizeof(short);
            return true;
        }

        return TryReadBigEndianSlow(out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBigEndianCore(out int value)
    {
        if (_span.Length - _index >= sizeof(int))
        {
            value = BinaryPrimitives.ReadInt32BigEndian(_span.Slice(_index));
            _index += sizeof(int);
            return true;
        }

        return TryReadBigEndianSlow(out value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadBigEndianCore(out long value)
    {
        if (_span.Length - _index >= sizeof(long))
        {
            value = BinaryPrimitives.ReadInt64BigEndian(_span.Slice(_index));
            _index += sizeof(long);
            return true;
        }

        return TryReadBigEndianSlow(out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadBigEndianSlow(out short value)
    {
        Span<byte> temp = stackalloc byte[sizeof(short)];
        var ok = TryReadSlow(temp);
        value = ok ? BinaryPrimitives.ReadInt16BigEndian(temp) : (short)0;
        return ok;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadBigEndianSlow(out int value)
    {
        Span<byte> temp = stackalloc byte[sizeof(int)];
        var ok = TryReadSlow(temp);
        value = ok ? BinaryPrimitives.ReadInt32BigEndian(temp) : 0;
        return ok;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryReadBigEndianSlow(out long value)
    {
        Span<byte> temp = stackalloc byte[sizeof(long)];
        var ok = TryReadSlow(temp);
        value = ok ? BinaryPrimitives.ReadInt64BigEndian(temp) : 0L;
        return ok;
    }

    /// <summary>
    /// Copies and consumes <paramref name="destination"/>.Length bytes. Returns false (position unchanged) if not
    /// enough bytes remain.
    /// </summary>
    private bool TryReadSlow(scoped Span<byte> destination)
    {
        if (!TryCopyToCore(destination))
            return false;

        AdvanceCore(destination.Length);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool TryCopyToCore(scoped Span<byte> destination)
    {
        var unread = _span.Slice(_index);
        if (unread.Length >= destination.Length)
        {
            unread.Slice(0, destination.Length).CopyTo(destination);
            return true;
        }

        return TryCopyToSlow(destination);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly bool TryCopyToSlow(scoped Span<byte> destination)
    {
        if (RemainingCore < destination.Length)
            return false;

        _sequence.Slice(PositionCore, destination.Length).CopyTo(destination);
        return true;
    }

    // ---------------------------------------------------------------- Depth

    /// <summary>
    /// Enters a nested container. Throws when the maximum depth is exceeded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enter()
    {
        if (++_depth > _options.MaxDepth)
            throw NexusSerializationException.DepthExceeded(_options.MaxDepth);
    }

    /// <summary>
    /// Leaves a nested container.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit()
    {
        _depth--;
    }

    // ---------------------------------------------------------------- Primitive access

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadCode()
    {
        if (!TryReadByteCore(out var code))
            throw NexusSerializationException.Truncated();
        return code;
    }

    /// <summary>
    /// Returns the next type code without consuming it.
    /// </summary>
    public readonly byte PeekCode()
    {
        if (!TryPeekByteCore(out var code))
            throw NexusSerializationException.Truncated();
        return code;
    }

    /// <summary>
    /// Reads one raw byte (not a MessagePack value).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadRawByte()
    {
        if (!TryReadByteCore(out var value))
            throw NexusSerializationException.Truncated();
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private short ReadRawInt16()
    {
        if (!TryReadBigEndianCore(out short value))
            throw NexusSerializationException.Truncated();
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadRawInt32()
    {
        if (!TryReadBigEndianCore(out int value))
            throw NexusSerializationException.Truncated();
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadRawInt64()
    {
        if (!TryReadBigEndianCore(out long value))
            throw NexusSerializationException.Truncated();
        return value;
    }

    // ---------------------------------------------------------------- Nil / Bool

    /// <summary>
    /// True when the next value is nil (does not consume).
    /// </summary>
    public readonly bool IsNil => TryPeekByteCore(out var code) && code == MsgPackCode.Nil;

    /// <summary>
    /// Consumes a nil value if one is next.
    /// </summary>
    /// <returns>True if nil was consumed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadNil()
    {
        if (TryPeekByteCore(out var code) && code == MsgPackCode.Nil)
        {
            AdvanceCore(1);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a nil value.
    /// </summary>
    public void ReadNil()
    {
        var code = ReadCode();
        if (code != MsgPackCode.Nil)
            throw NexusSerializationException.UnexpectedCode(code, "nil");
    }

    /// <summary>
    /// Reads a boolean.
    /// </summary>
    public bool ReadBoolean()
    {
        var code = ReadCode();
        return code switch
        {
            MsgPackCode.True => true,
            MsgPackCode.False => false,
            _ => throw NexusSerializationException.UnexpectedCode(code, "bool")
        };
    }

    // ---------------------------------------------------------------- Integers

    /// <summary>
    /// Reads any MessagePack integer form as a signed 64-bit value.
    /// </summary>
    public long ReadInt64()
    {
        var code = ReadCode();
        if (code <= MsgPackCode.MaxFixPositive)
            return code;
        if (code >= MsgPackCode.MinFixNegative)
            return unchecked((sbyte)code);

        switch (code)
        {
            case MsgPackCode.UInt8: return ReadRawByte();
            case MsgPackCode.UInt16: return unchecked((ushort)ReadRawInt16());
            case MsgPackCode.UInt32: return unchecked((uint)ReadRawInt32());
            case MsgPackCode.UInt64:
                var u = unchecked((ulong)ReadRawInt64());
                if (u > long.MaxValue)
                    throw NexusSerializationException.Overflow("Int64", new OverflowException());
                return (long)u;
            case MsgPackCode.Int8: return unchecked((sbyte)ReadRawByte());
            case MsgPackCode.Int16: return ReadRawInt16();
            case MsgPackCode.Int32: return ReadRawInt32();
            case MsgPackCode.Int64: return ReadRawInt64();
            default: throw NexusSerializationException.UnexpectedCode(code, "integer");
        }
    }

    /// <summary>
    /// Reads any MessagePack integer form as an unsigned 64-bit value.
    /// </summary>
    public ulong ReadUInt64()
    {
        var code = ReadCode();
        if (code <= MsgPackCode.MaxFixPositive)
            return code;

        long signed;
        switch (code)
        {
            case MsgPackCode.UInt8: return ReadRawByte();
            case MsgPackCode.UInt16: return unchecked((ushort)ReadRawInt16());
            case MsgPackCode.UInt32: return unchecked((uint)ReadRawInt32());
            case MsgPackCode.UInt64: return unchecked((ulong)ReadRawInt64());
            case MsgPackCode.Int8: signed = unchecked((sbyte)ReadRawByte()); break;
            case MsgPackCode.Int16: signed = ReadRawInt16(); break;
            case MsgPackCode.Int32: signed = ReadRawInt32(); break;
            case MsgPackCode.Int64: signed = ReadRawInt64(); break;
            default:
                if (code >= MsgPackCode.MinFixNegative)
                    throw NexusSerializationException.Overflow("UInt64", new OverflowException());
                throw NexusSerializationException.UnexpectedCode(code, "integer");
        }

        if (signed < 0)
            throw NexusSerializationException.Overflow("UInt64", new OverflowException());
        return (ulong)signed;
    }

    /// <summary>
    /// Reads a signed 32-bit integer from any integer form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        // Fast path for positive and negative fixint.
        var span = _span;
        var index = _index;
        if ((uint)index < (uint)span.Length)
        {
            var code = span[index];
            if (code <= MsgPackCode.MaxFixPositive || code >= MsgPackCode.MinFixNegative)
            {
                _index = index + 1;
                return code <= MsgPackCode.MaxFixPositive ? code : unchecked((sbyte)code);
            }
        }

        return ReadInt32Slow();
    }

    private int ReadInt32Slow()
    {
        // Sized forms decoded from the span; anything else (segment boundary, 64-bit forms, errors) falls back.
        var span = _span;
        var index = _index;
        if (span.Length - index >= 5)
        {
            var data = span.Slice(index + 1);
            switch (span[index])
            {
                case MsgPackCode.UInt8:
                    _index = index + 2;
                    return data[0];
                case MsgPackCode.UInt16:
                    _index = index + 3;
                    return BinaryPrimitives.ReadUInt16BigEndian(data);
                case MsgPackCode.Int8:
                    _index = index + 2;
                    return unchecked((sbyte)data[0]);
                case MsgPackCode.Int16:
                    _index = index + 3;
                    return BinaryPrimitives.ReadInt16BigEndian(data);
                case MsgPackCode.Int32:
                    _index = index + 5;
                    return BinaryPrimitives.ReadInt32BigEndian(data);
            }
        }

        return ReadInt32Fallback();
    }

    private int ReadInt32Fallback()
    {
        var value = ReadInt64();
        if (value < int.MinValue || value > int.MaxValue)
            throw NexusSerializationException.Overflow("Int32", new OverflowException());
        return (int)value;
    }

    /// <summary>
    /// Reads an unsigned 32-bit integer from any integer form.
    /// </summary>
    public uint ReadUInt32()
    {
        var value = ReadUInt64();
        if (value > uint.MaxValue)
            throw NexusSerializationException.Overflow("UInt32", new OverflowException());
        return (uint)value;
    }

    /// <summary>
    /// Reads a signed 16-bit integer from any integer form.
    /// </summary>
    public short ReadInt16()
    {
        var value = ReadInt64();
        if (value < short.MinValue || value > short.MaxValue)
            throw NexusSerializationException.Overflow("Int16", new OverflowException());
        return (short)value;
    }

    /// <summary>
    /// Reads an unsigned 16-bit integer from any integer form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        if (TryPeekByteCore(out var code) && code <= MsgPackCode.MaxFixPositive)
        {
            AdvanceCore(1);
            return code;
        }

        var value = ReadUInt64();
        if (value > ushort.MaxValue)
            throw NexusSerializationException.Overflow("UInt16", new OverflowException());
        return (ushort)value;
    }

    /// <summary>
    /// Reads a signed 8-bit integer from any integer form.
    /// </summary>
    public sbyte ReadSByte()
    {
        var value = ReadInt64();
        if (value < sbyte.MinValue || value > sbyte.MaxValue)
            throw NexusSerializationException.Overflow("SByte", new OverflowException());
        return (sbyte)value;
    }

    /// <summary>
    /// Reads an unsigned 8-bit integer from any integer form.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (TryPeekByteCore(out var code) && code <= MsgPackCode.MaxFixPositive)
        {
            AdvanceCore(1);
            return code;
        }

        var value = ReadUInt64();
        if (value > byte.MaxValue)
            throw NexusSerializationException.Overflow("Byte", new OverflowException());
        return (byte)value;
    }

    /// <summary>
    /// Reads a UTF-16 code unit from any integer form.
    /// </summary>
    public char ReadChar() => (char)ReadUInt16();

    // ---------------------------------------------------------------- Floating point

    /// <summary>
    /// Reads a 32-bit float. Accepts float32, float64 and integer forms.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadSingle()
    {
        var span = _span;
        var index = _index;
        if ((uint)index < (uint)span.Length && span[index] == MsgPackCode.Float32 && span.Length - index > sizeof(float))
        {
            _index = index + 1 + sizeof(float);
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(span.Slice(index + 1)));
        }

        return ReadSingleSlow();
    }

    private float ReadSingleSlow()
    {
        var code = PeekCode();
        switch (code)
        {
            case MsgPackCode.Float32:
                AdvanceCore(1);
                return BitConverter.Int32BitsToSingle(ReadRawInt32());
            case MsgPackCode.Float64:
                AdvanceCore(1);
                return (float)BitConverter.Int64BitsToDouble(ReadRawInt64());
            case MsgPackCode.UInt64:
                return ReadUInt64();
            default:
                return ReadInt64();
        }
    }

    /// <summary>
    /// Reads a 64-bit float. Accepts float64, float32 and integer forms.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble()
    {
        var span = _span;
        var index = _index;
        if ((uint)index < (uint)span.Length && span[index] == MsgPackCode.Float64 && span.Length - index > sizeof(double))
        {
            _index = index + 1 + sizeof(double);
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(span.Slice(index + 1)));
        }

        return ReadDoubleSlow();
    }

    private double ReadDoubleSlow()
    {
        var code = PeekCode();
        switch (code)
        {
            case MsgPackCode.Float64:
                AdvanceCore(1);
                return BitConverter.Int64BitsToDouble(ReadRawInt64());
            case MsgPackCode.Float32:
                AdvanceCore(1);
                return BitConverter.Int32BitsToSingle(ReadRawInt32());
            case MsgPackCode.UInt64:
                return ReadUInt64();
            default:
                return ReadInt64();
        }
    }

    // ---------------------------------------------------------------- Containers

    /// <summary>
    /// Reads an array header. The count is validated against the remaining bytes (each element needs at least
    /// one byte) before the caller allocates anything.
    /// </summary>
    public int ReadArrayHeader()
    {
        var code = ReadCode();
        long count;
        if (code >= MsgPackCode.MinFixArray && code <= MsgPackCode.MaxFixArray)
            count = code & 0x0f;
        else if (code == MsgPackCode.Array16)
            count = unchecked((ushort)ReadRawInt16());
        else if (code == MsgPackCode.Array32)
            count = unchecked((uint)ReadRawInt32());
        else
            throw NexusSerializationException.UnexpectedCode(code, "array");

        if (count > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(count, RemainingCore);

        return (int)count;
    }

    /// <summary>
    /// Reads an array header and requires exactly <paramref name="expected"/> elements.
    /// </summary>
    public void ReadArrayHeader(int expected)
    {
        var count = ReadArrayHeader();
        if (count != expected)
            throw NexusSerializationException.UnexpectedCount(expected, count);
    }

    /// <summary>
    /// Reads a map header. The pair count is validated against the remaining bytes.
    /// </summary>
    public int ReadMapHeader()
    {
        var code = ReadCode();
        long count;
        if (code >= MsgPackCode.MinFixMap && code <= MsgPackCode.MaxFixMap)
            count = code & 0x0f;
        else if (code == MsgPackCode.Map16)
            count = unchecked((ushort)ReadRawInt16());
        else if (code == MsgPackCode.Map32)
            count = unchecked((uint)ReadRawInt32());
        else
            throw NexusSerializationException.UnexpectedCode(code, "map");

        if (count * 2 > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(count * 2, RemainingCore);

        return (int)count;
    }

    // ---------------------------------------------------------------- Strings

    /// <summary>
    /// Reads a string or nil.
    /// </summary>
    public string? ReadString()
    {
        var code = ReadCode();
        int length;
        if (code >= MsgPackCode.MinFixStr && code <= MsgPackCode.MaxFixStr)
            length = code & 0x1f;
        else if (code == MsgPackCode.Nil)
            return null;
        else
            length = ReadStringLength(code);

        return DecodeString(length);
    }

    private int ReadStringLength(byte code)
    {
        long length = code switch
        {
            MsgPackCode.Str8 => ReadRawByte(),
            MsgPackCode.Str16 => unchecked((ushort)ReadRawInt16()),
            MsgPackCode.Str32 => unchecked((uint)ReadRawInt32()),
            _ => throw NexusSerializationException.UnexpectedCode(code, "string")
        };

        if (length > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(length, RemainingCore);

        return (int)length;
    }

    private string DecodeString(int length)
    {
        if (length == 0)
            return string.Empty;

        if (length > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(length, RemainingCore);

        string result;
        var unread = UnreadSpanCore;
        if (unread.Length >= length)
        {
            result = DecodeUtf8(unread.Slice(0, length), _options.StrictUtf8);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                _sequence.Slice(PositionCore, length).CopyTo(rented);
                result = DecodeUtf8(rented.AsSpan(0, length), _options.StrictUtf8);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        AdvanceCore(length);
        return result;
    }

    /// <summary>
    /// Decodes UTF-8. ASCII (the common case) is validated and widened straight into the new string. Other text is
    /// transcoded in one pass into a temporary buffer (a UTF-16 string never has more chars than UTF-8 bytes).
    /// Invalid UTF-8 throws when <paramref name="strict"/> is set and is replaced with U+FFFD otherwise.
    /// </summary>
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes, bool strict)
    {
        if (Ascii.IsValid(bytes))
            return string.Create(bytes.Length, bytes, static (chars, source) => Ascii.ToUtf16(source, chars, out _));

        char[]? rented = null;
        var buffer = bytes.Length <= StackDecodeLimit
            ? stackalloc char[StackDecodeLimit]
            : (rented = ArrayPool<char>.Shared.Rent(bytes.Length));
        try
        {
            var status = Utf8.ToUtf16(bytes, buffer, out _, out var written, replaceInvalidSequences: !strict);
            if (status != OperationStatus.Done)
                throw NexusSerializationException.InvalidUtf8(new DecoderFallbackException($"UTF-8 decoding stopped: {status}."));

            return new string(buffer.Slice(0, written));
        }
        finally
        {
            if (rented != null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    // ---------------------------------------------------------------- Binary

    /// <summary>
    /// Reads the length of a bin value. Also accepts str forms (old-spec writers used raw strings for bytes).
    /// Returns -1 for nil.
    /// </summary>
    public int ReadBinaryHeader()
    {
        var code = ReadCode();
        long length;
        switch (code)
        {
            case MsgPackCode.Nil: return -1;
            case MsgPackCode.Bin8:
            case MsgPackCode.Str8:
                length = ReadRawByte();
                break;
            case MsgPackCode.Bin16:
            case MsgPackCode.Str16:
                length = unchecked((ushort)ReadRawInt16());
                break;
            case MsgPackCode.Bin32:
            case MsgPackCode.Str32:
                length = unchecked((uint)ReadRawInt32());
                break;
            default:
                if (code >= MsgPackCode.MinFixStr && code <= MsgPackCode.MaxFixStr)
                {
                    length = code & 0x1f;
                    break;
                }

                throw NexusSerializationException.UnexpectedCode(code, "binary");
        }

        if (length > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(length, RemainingCore);

        return (int)length;
    }

    /// <summary>
    /// Reads a bin value as a slice of the underlying sequence (no copy). Returns null for nil.
    /// </summary>
    public ReadOnlySequence<byte>? ReadBinary()
    {
        var length = ReadBinaryHeader();
        if (length < 0)
            return null;

        return ReadRawBytes(length);
    }

    /// <summary>
    /// Reads a bin value into a newly allocated array. Returns null for nil.
    /// </summary>
    public byte[]? ReadBinaryToArray()
    {
        var length = ReadBinaryHeader();
        if (length < 0)
            return null;
        if (length == 0)
            return Array.Empty<byte>();

        var result = GC.AllocateUninitializedArray<byte>(length);
        ReadRawBytes(length).CopyTo(result);
        return result;
    }

    /// <summary>
    /// Reads a bin value into an array rented from <see cref="ArrayPool{T}.Shared"/>.
    /// Returns default for nil. The caller owns the rented array.
    /// </summary>
    public Memory<byte> ReadBinaryToPooled(out bool isNil)
    {
        var length = ReadBinaryHeader();
        isNil = length < 0;
        if (length <= 0)
            return default;

        var rented = ArrayPool<byte>.Shared.Rent(length);
        ReadRawBytes(length).CopyTo(rented);
        return rented.AsMemory(0, length);
    }

    /// <summary>
    /// Consumes <paramref name="length"/> raw bytes and returns them as a slice (no copy).
    /// </summary>
    public ReadOnlySequence<byte> ReadRawBytes(long length)
    {
        if (length > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(length, RemainingCore);

        var slice = _sequence.Slice(PositionCore, length);
        AdvanceCore(length);
        return slice;
    }

    /// <summary>
    /// Copies raw bytes into <paramref name="destination"/>, consuming them.
    /// </summary>
    public void ReadRawBytes(scoped Span<byte> destination)
    {
        if (!TryCopyToCore(destination))
            throw NexusSerializationException.Truncated();
        AdvanceCore(destination.Length);
    }

    // ---------------------------------------------------------------- Ext

    /// <summary>
    /// Reads an ext header and returns the data length.
    /// </summary>
    public int ReadExtHeader(out sbyte typeCode)
    {
        var code = ReadCode();
        long length;
        switch (code)
        {
            case MsgPackCode.FixExt1: length = 1; break;
            case MsgPackCode.FixExt2: length = 2; break;
            case MsgPackCode.FixExt4: length = 4; break;
            case MsgPackCode.FixExt8: length = 8; break;
            case MsgPackCode.FixExt16: length = 16; break;
            case MsgPackCode.Ext8: length = ReadRawByte(); break;
            case MsgPackCode.Ext16: length = unchecked((ushort)ReadRawInt16()); break;
            case MsgPackCode.Ext32: length = unchecked((uint)ReadRawInt32()); break;
            default: throw NexusSerializationException.UnexpectedCode(code, "ext");
        }

        typeCode = unchecked((sbyte)ReadRawByte());
        if (length > RemainingCore)
            throw NexusSerializationException.LengthExceedsRemaining(length, RemainingCore);

        return (int)length;
    }

    // ---------------------------------------------------------------- Skip / raw values

    /// <summary>
    /// Advances past exactly one MessagePack value.
    /// Returns false (position unchanged) if the data ends before the value is complete.
    /// Throws on malformed data (e.g. 0xc1).
    /// </summary>
    /// <remarks>
    /// Iterative, with a pending-value counter instead of recursion, so deeply nested input costs no stack.
    /// The loop is bounded by the input length because every pending value needs at least one byte.
    /// A value inside the current segment is skipped on the span alone; one that crosses segments (or is
    /// incomplete) takes the slower path that walks the sequence.
    /// </remarks>
    public bool TrySkip()
    {
        var end = SkipInSpan(_span, _index);
        if (end >= 0)
        {
            _index = end;
            return true;
        }

        return TrySkipAcrossSegments();
    }

    /// <summary>
    /// Gets the length in bytes of the next value without consuming it. Returns false if the value is incomplete.
    /// Throws on malformed data (e.g. 0xc1).
    /// </summary>
    internal readonly bool TryGetNextValueLength(out long length)
    {
        var end = SkipInSpan(_span, _index);
        if (end >= 0)
        {
            length = end - _index;
            return true;
        }

        var probe = this;
        if (!probe.TrySkipAcrossSegments())
        {
            length = 0;
            return false;
        }

        length = probe.ConsumedCore - ConsumedCore;
        return true;
    }

    /// <summary>
    /// Skips one value that lies entirely within <paramref name="span"/> starting at <paramref name="index"/>.
    /// Returns the index after the value, or -1 if the value runs past the end of the span (it may be incomplete or
    /// continue in the next segment). Throws on 0xc1.
    /// </summary>
    private static int SkipInSpan(ReadOnlySpan<byte> span, int index)
    {
        long pending = 1;
        while (pending > 0)
        {
            if ((uint)index >= (uint)span.Length)
                return -1;

            var code = span[index++];
            pending--;
            long skipBytes;
            switch (code)
            {
                case <= MsgPackCode.MaxFixPositive:
                case >= MsgPackCode.MinFixNegative:
                case MsgPackCode.Nil:
                case MsgPackCode.False:
                case MsgPackCode.True:
                    continue;
                case >= MsgPackCode.MinFixMap and <= MsgPackCode.MaxFixMap:
                    pending += 2L * (code & 0x0f);
                    continue;
                case >= MsgPackCode.MinFixArray and <= MsgPackCode.MaxFixArray:
                    pending += code & 0x0f;
                    continue;
                case >= MsgPackCode.MinFixStr and <= MsgPackCode.MaxFixStr:
                    skipBytes = code & 0x1f;
                    break;
                case MsgPackCode.UInt8:
                case MsgPackCode.Int8:
                    skipBytes = 1;
                    break;
                case MsgPackCode.UInt16:
                case MsgPackCode.Int16:
                    skipBytes = 2;
                    break;
                case MsgPackCode.Float32:
                case MsgPackCode.UInt32:
                case MsgPackCode.Int32:
                    skipBytes = 4;
                    break;
                case MsgPackCode.Float64:
                case MsgPackCode.UInt64:
                case MsgPackCode.Int64:
                    skipBytes = 8;
                    break;
                case MsgPackCode.FixExt1: skipBytes = 2; break;
                case MsgPackCode.FixExt2: skipBytes = 3; break;
                case MsgPackCode.FixExt4: skipBytes = 5; break;
                case MsgPackCode.FixExt8: skipBytes = 9; break;
                case MsgPackCode.FixExt16: skipBytes = 17; break;
                case MsgPackCode.Bin8:
                case MsgPackCode.Str8:
                    if (span.Length - index < 1) return -1;
                    skipBytes = 1 + span[index];
                    break;
                case MsgPackCode.Bin16:
                case MsgPackCode.Str16:
                    if (span.Length - index < 2) return -1;
                    skipBytes = 2 + BinaryPrimitives.ReadUInt16BigEndian(span.Slice(index));
                    break;
                case MsgPackCode.Bin32:
                case MsgPackCode.Str32:
                    if (span.Length - index < 4) return -1;
                    skipBytes = 4L + BinaryPrimitives.ReadUInt32BigEndian(span.Slice(index));
                    break;
                case MsgPackCode.Ext8:
                    if (span.Length - index < 1) return -1;
                    skipBytes = 1 + 1 + span[index]; // length, ext type, data
                    break;
                case MsgPackCode.Ext16:
                    if (span.Length - index < 2) return -1;
                    skipBytes = 2 + 1 + BinaryPrimitives.ReadUInt16BigEndian(span.Slice(index));
                    break;
                case MsgPackCode.Ext32:
                    if (span.Length - index < 4) return -1;
                    skipBytes = 4L + 1 + BinaryPrimitives.ReadUInt32BigEndian(span.Slice(index));
                    break;
                case MsgPackCode.Array16:
                    if (span.Length - index < 2) return -1;
                    pending += BinaryPrimitives.ReadUInt16BigEndian(span.Slice(index));
                    skipBytes = 2;
                    break;
                case MsgPackCode.Array32:
                    if (span.Length - index < 4) return -1;
                    pending += BinaryPrimitives.ReadUInt32BigEndian(span.Slice(index));
                    skipBytes = 4;
                    break;
                case MsgPackCode.Map16:
                    if (span.Length - index < 2) return -1;
                    pending += 2L * BinaryPrimitives.ReadUInt16BigEndian(span.Slice(index));
                    skipBytes = 2;
                    break;
                case MsgPackCode.Map32:
                    if (span.Length - index < 4) return -1;
                    pending += 2L * BinaryPrimitives.ReadUInt32BigEndian(span.Slice(index));
                    skipBytes = 4;
                    break;
                default:
                    throw NexusSerializationException.UnexpectedCode(code, "any"); // 0xc1
            }

            if (skipBytes > span.Length - index)
                return -1;

            index += (int)skipBytes;
        }

        return index;
    }

    /// <summary>
    /// <see cref="TrySkip"/> for values that cross segment boundaries or may be incomplete.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TrySkipAcrossSegments()
    {
        var r = this;
        long pending = 1;
        while (pending > 0)
        {
            if (!r.TryReadByteCore(out var code))
                return false;

            pending--;
            long skipBytes;
            switch (code)
            {
                case <= MsgPackCode.MaxFixPositive:
                case >= MsgPackCode.MinFixNegative:
                case MsgPackCode.Nil:
                case MsgPackCode.False:
                case MsgPackCode.True:
                    skipBytes = 0;
                    break;
                case >= MsgPackCode.MinFixMap and <= MsgPackCode.MaxFixMap:
                    pending += 2L * (code & 0x0f);
                    skipBytes = 0;
                    break;
                case >= MsgPackCode.MinFixArray and <= MsgPackCode.MaxFixArray:
                    pending += code & 0x0f;
                    skipBytes = 0;
                    break;
                case >= MsgPackCode.MinFixStr and <= MsgPackCode.MaxFixStr:
                    skipBytes = code & 0x1f;
                    break;
                case MsgPackCode.UInt8:
                case MsgPackCode.Int8:
                    skipBytes = 1;
                    break;
                case MsgPackCode.UInt16:
                case MsgPackCode.Int16:
                    skipBytes = 2;
                    break;
                case MsgPackCode.Float32:
                case MsgPackCode.UInt32:
                case MsgPackCode.Int32:
                    skipBytes = 4;
                    break;
                case MsgPackCode.Float64:
                case MsgPackCode.UInt64:
                case MsgPackCode.Int64:
                    skipBytes = 8;
                    break;
                case MsgPackCode.FixExt1: skipBytes = 2; break;
                case MsgPackCode.FixExt2: skipBytes = 3; break;
                case MsgPackCode.FixExt4: skipBytes = 5; break;
                case MsgPackCode.FixExt8: skipBytes = 9; break;
                case MsgPackCode.FixExt16: skipBytes = 17; break;
                case MsgPackCode.Bin8:
                case MsgPackCode.Str8:
                    if (!TryReadLength8(ref r, out skipBytes)) return false;
                    break;
                case MsgPackCode.Bin16:
                case MsgPackCode.Str16:
                    if (!TryReadLength16(ref r, out skipBytes)) return false;
                    break;
                case MsgPackCode.Bin32:
                case MsgPackCode.Str32:
                    if (!TryReadLength32(ref r, out skipBytes)) return false;
                    break;
                case MsgPackCode.Ext8:
                    if (!TryReadLength8(ref r, out skipBytes)) return false;
                    skipBytes += 1; // ext type byte
                    break;
                case MsgPackCode.Ext16:
                    if (!TryReadLength16(ref r, out skipBytes)) return false;
                    skipBytes += 1;
                    break;
                case MsgPackCode.Ext32:
                    if (!TryReadLength32(ref r, out skipBytes)) return false;
                    skipBytes += 1;
                    break;
                case MsgPackCode.Array16:
                    if (!TryReadLength16(ref r, out var a16)) return false;
                    pending += a16;
                    skipBytes = 0;
                    break;
                case MsgPackCode.Array32:
                    if (!TryReadLength32(ref r, out var a32)) return false;
                    pending += a32;
                    skipBytes = 0;
                    break;
                case MsgPackCode.Map16:
                    if (!TryReadLength16(ref r, out var m16)) return false;
                    pending += 2 * m16;
                    skipBytes = 0;
                    break;
                case MsgPackCode.Map32:
                    if (!TryReadLength32(ref r, out var m32)) return false;
                    pending += 2 * m32;
                    skipBytes = 0;
                    break;
                default:
                    throw NexusSerializationException.UnexpectedCode(code, "any"); // 0xc1
            }

            if (r.RemainingCore < skipBytes)
                return false;

            r.AdvanceCore(skipBytes);
        }

        this = r;
        return true;
    }


    /// <summary>
    /// Advances past exactly one value. Throws if the data is truncated.
    /// </summary>
    public void Skip()
    {
        if (!TrySkip())
            throw NexusSerializationException.Truncated();
    }

    /// <summary>
    /// Consumes exactly one value and returns its raw bytes as a slice of the underlying sequence.
    /// Throws if the data is truncated or malformed.
    /// </summary>
    public ReadOnlySequence<byte> ReadRawValue()
    {
        var start = PositionCore;
        Skip();
        return _sequence.Slice(start, PositionCore);
    }

    private static bool TryReadLength8(ref MsgPackReader r, out long length)
    {
        if (!r.TryReadByteCore(out var b))
        {
            length = 0;
            return false;
        }

        length = b;
        return true;
    }

    private static bool TryReadLength16(ref MsgPackReader r, out long length)
    {
        if (!r.TryReadBigEndianCore(out short v))
        {
            length = 0;
            return false;
        }

        length = unchecked((ushort)v);
        return true;
    }

    private static bool TryReadLength32(ref MsgPackReader r, out long length)
    {
        if (!r.TryReadBigEndianCore(out int v))
        {
            length = 0;
            return false;
        }

        length = unchecked((uint)v);
        return true;
    }
}
