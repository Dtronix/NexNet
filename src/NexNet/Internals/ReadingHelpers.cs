using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace NexNet.Internals;

internal static class ReadingHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// Reads a little-endian ushort (framing fields are always little-endian).
    /// </summary>
    public static bool TryReadUShort(in ReadOnlySequence<byte> sequence, Span<byte> buffer, ref int position, out ushort value)
    {
        if (!TryRead(sequence, buffer, ref position, 2, out var spanValue))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(spanValue);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead(in ReadOnlySequence<byte> sequence, Span<byte> buffer, ref int position, int size, out ReadOnlySpan<byte> value)
    {
        try
        {
            var valueSlice = sequence.Slice(position, size);
            position += size;
            // If this is a single segment, we can just treat it like a single span.
            // If we cross multiple spans, we need to copy the memory into a single
            // continuous span.

            if (valueSlice.IsSingleSegment)
            {
                value = valueSlice.FirstSpan;
            }
            else
            {
                valueSlice.CopyTo(buffer);
                value = buffer;
            }

            return true;
        }
        catch
        {
            value = ReadOnlySpan<byte>.Empty;
            return false;
        }
    }
}
