using System.Buffers;

namespace NexNet.Inspector.Core.Assembly;

/// <summary>
/// The bytes of one direction of a tapped stream, addressed by absolute stream offset (the offsets a
/// <see cref="Framing.FrameRecord"/> holds).
/// </summary>
/// <remarks>
/// Frame records hold positions only, so assembly reads bodies back through this. An implementation hands out the
/// buffers it already retains (a live capture's ring of transport ops, or the op blocks of a recording) rather than
/// copying them into one contiguous stream; a range that crosses two ops comes back in two segments.
/// </remarks>
public interface IStreamBytes
{
    /// <summary>Offset of the oldest byte still available.</summary>
    long StartOffset { get; }

    /// <summary>Offset one past the newest byte available.</summary>
    long EndOffset { get; }

    /// <summary>Gets the contiguous bytes that start at <paramref name="offset"/>.</summary>
    /// <param name="offset">Absolute stream offset.</param>
    /// <param name="segment">
    /// Bytes from <paramref name="offset"/> to the end of the buffer that holds it; it may end before
    /// <see cref="EndOffset"/>. It stays valid while the offset is not trimmed away.
    /// </param>
    /// <returns>False when the offset is outside the available range.</returns>
    bool TryGetSegment(long offset, out ReadOnlyMemory<byte> segment);
}

/// <summary>Range helpers over <see cref="IStreamBytes"/>.</summary>
public static class StreamBytesExtensions
{
    /// <summary>Copies a range into <paramref name="destination"/>, which it fills exactly.</summary>
    /// <param name="bytes">The stream.</param>
    /// <param name="offset">Absolute offset of the first byte.</param>
    /// <param name="destination">Receives the bytes; its length is the range length.</param>
    /// <returns>False when part of the range is not available.</returns>
    public static bool TryCopyTo(this IStreamBytes bytes, long offset, Span<byte> destination)
    {
        if (offset < bytes.StartOffset || offset + destination.Length > bytes.EndOffset)
            return false;

        while (!destination.IsEmpty)
        {
            if (!bytes.TryGetSegment(offset, out var segment) || segment.IsEmpty)
                return false;

            var take = Math.Min(segment.Length, destination.Length);
            segment.Span.Slice(0, take).CopyTo(destination);
            destination = destination.Slice(take);
            offset += take;
        }

        return true;
    }

    /// <summary>Gets a range as a sequence over the stream's own buffers.</summary>
    /// <param name="bytes">The stream.</param>
    /// <param name="offset">Absolute offset of the first byte.</param>
    /// <param name="length">Length of the range.</param>
    /// <param name="sequence">The range. A range inside one buffer costs no allocation.</param>
    /// <returns>False when part of the range is not available.</returns>
    public static bool TryGetSequence(this IStreamBytes bytes, long offset, long length, out ReadOnlySequence<byte> sequence)
    {
        sequence = ReadOnlySequence<byte>.Empty;
        if (length < 0 || offset < bytes.StartOffset || offset + length > bytes.EndOffset)
            return false;
        if (length == 0)
            return true;
        if (!bytes.TryGetSegment(offset, out var first) || first.IsEmpty)
            return false;
        if (first.Length >= length)
        {
            sequence = new ReadOnlySequence<byte>(first.Slice(0, (int)length));
            return true;
        }

        var builder = new SequenceBuilder();
        if (!builder.Append(bytes, offset, length))
            return false;
        sequence = builder.Build();
        return true;
    }
}

/// <summary>Chains buffers into a <see cref="ReadOnlySequence{T}"/>; allocates one segment per buffer.</summary>
internal struct SequenceBuilder
{
    private Segment? _first;
    private Segment? _last;
    private ReadOnlyMemory<byte> _single;
    private int _count;

    public void Append(ReadOnlyMemory<byte> memory)
    {
        if (memory.IsEmpty)
            return;

        _count++;
        if (_count == 1)
        {
            // Held back until a second buffer arrives, so a one-buffer result needs no segment object.
            _single = memory;
            return;
        }

        if (_count == 2)
        {
            _first = new Segment(_single, 0);
            _last = _first;
        }

        _last = _last!.Append(memory);
    }

    public bool Append(IStreamBytes bytes, long offset, long length)
    {
        while (length > 0)
        {
            if (!bytes.TryGetSegment(offset, out var segment) || segment.IsEmpty)
                return false;

            var take = (int)Math.Min(segment.Length, length);
            Append(segment.Slice(0, take));
            offset += take;
            length -= take;
        }

        return true;
    }

    public readonly ReadOnlySequence<byte> Build() => _count switch
    {
        0 => ReadOnlySequence<byte>.Empty,
        1 => new ReadOnlySequence<byte>(_single),
        _ => new ReadOnlySequence<byte>(_first!, 0, _last!, _last!.Memory.Length),
    };

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
