using System;
using System.Buffers;
using NexNet.Serialization;

namespace NexNet.Pipes;

/// <summary>
/// The channel item prefix (docs/internals/flow-control.md, section 4.5): the item's length in bytes as an
/// unsigned LEB128 varint (7 bits per byte, low bits first, high bit set on every byte but the last).
/// </summary>
internal static class ChannelFraming
{
    /// <summary>
    /// Longest prefix: five bytes hold any length up to <see cref="int.MaxValue"/>.
    /// </summary>
    public const int MaxPrefixLength = 5;

    /// <summary>
    /// Bytes needed to write <paramref name="length"/> as a prefix.
    /// </summary>
    public static int PrefixLength(int length)
    {
        var bytes = 1;
        for (var value = (uint)length; value >= 0x80; value >>= 7)
            bytes++;
        return bytes;
    }

    /// <summary>
    /// Writes <paramref name="length"/> as a prefix at the start of <paramref name="destination"/>. Returns the bytes
    /// written.
    /// </summary>
    public static int WriteLength(Span<byte> destination, int length)
    {
        var value = (uint)length;
        var i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }

    /// <summary>
    /// Reads a prefix. Returns false if the reader's data ends inside it; the reader is then left inside the prefix.
    /// </summary>
    /// <exception cref="NexusSerializationException">The prefix is longer than five bytes or exceeds
    /// <see cref="int.MaxValue"/>.</exception>
    public static bool TryReadLength(ref MsgPackReader reader, out int length)
    {
        length = 0;
        ulong value = 0;
        for (var i = 0; i < MaxPrefixLength; i++)
        {
            if (reader.End)
                return false;

            var b = reader.ReadRawByte();
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (value > int.MaxValue)
                    throw new NexusSerializationException("Channel item length prefix exceeds the maximum length.");

                length = (int)value;
                return true;
            }
        }

        throw new NexusSerializationException("Channel item length prefix is longer than five bytes.");
    }

    /// <summary>
    /// Reads a prefix. Returns false if the buffer ends inside it, leaving <paramref name="reader"/> where it was.
    /// </summary>
    /// <exception cref="NexusSerializationException">The prefix is longer than five bytes or exceeds
    /// <see cref="int.MaxValue"/>.</exception>
    public static bool TryReadLength(ref SequenceReader<byte> reader, out int length)
    {
        length = 0;
        ulong value = 0;
        for (var i = 0; i < MaxPrefixLength; i++)
        {
            if (!reader.TryRead(out var b))
            {
                reader.Rewind(i);
                return false;
            }

            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (value > int.MaxValue)
                    throw new NexusSerializationException("Channel item length prefix exceeds the maximum length.");

                length = (int)value;
                return true;
            }
        }

        throw new NexusSerializationException("Channel item length prefix is longer than five bytes.");
    }
}
