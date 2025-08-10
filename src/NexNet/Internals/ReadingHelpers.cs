using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace NexNet.Internals;

internal static class ReadingHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadUShort(in ReadOnlySequence<byte> sequence, Span<byte> buffer, ref int position, out ushort value)
    {
        if (!TryRead(sequence, buffer, ref position, 2, out var spanValue))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt16(spanValue);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadULong(in ReadOnlySequence<byte> sequence, Span<byte> buffer, ref int position, out ulong value)
    {
        if (!TryRead(sequence, buffer, ref position, 8, out var spanValue))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt64(spanValue);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadInt(in ReadOnlySequence<byte> sequence, Span<byte> buffer, ref int position, out int value)
    {
        if (!TryRead(sequence, buffer, ref position, 4, out var spanValue))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToInt32(spanValue);
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReadUInt(in ReadOnlySequence<byte> sequence, Span<byte> buffer, ref int position, out uint value)
    {
        if (!TryRead(sequence, buffer, ref position, 4, out var spanValue))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt32(spanValue);
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
