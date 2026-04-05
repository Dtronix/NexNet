using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame signaling the start of a write operation.
/// Wire format: [Count:4]
/// </summary>
public readonly struct WriteFrame : IWritableFrame
{
    /// <summary>
    /// The wire size of this frame's payload.
    /// </summary>
    public const int Size = 4;

    /// <summary>
    /// The number of bytes that will be written.
    /// </summary>
    public int Count { get; init; }

    /// <summary>
    /// Creates a new Write frame.
    /// </summary>
    /// <param name="count">The number of bytes to write.</param>
    public WriteFrame(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative.");

        Count = count;
    }

    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    public int GetPayloadSize() => Size;

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        StreamBinaryHelpers.WriteInt32(buffer, Count);
        return Size;
    }

    /// <summary>
    /// Reads a Write frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed Write frame.</returns>
    public static WriteFrame Read(ReadOnlySpan<byte> buffer)
    {
        var count = StreamBinaryHelpers.ReadInt32(buffer);
        return new WriteFrame { Count = count };
    }

    /// <inheritdoc />
    public override string ToString() => $"WriteFrame {{ Count = {Count} }}";
}
