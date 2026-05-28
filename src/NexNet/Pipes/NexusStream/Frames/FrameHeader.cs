using System;
using System.Runtime.CompilerServices;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Represents the fixed 5-byte header for all NexStream frames.
/// Format: [Type:1][PayloadLength:4] (little-endian)
/// </summary>
public readonly struct FrameHeader
{
    /// <summary>
    /// The size of the frame header in bytes.
    /// </summary>
    public const int Size = 5;

    /// <summary>
    /// The frame type identifier.
    /// </summary>
    public FrameType Type { get; init; }

    /// <summary>
    /// The length of the frame payload in bytes.
    /// </summary>
    public int PayloadLength { get; init; }

    /// <summary>
    /// Creates a new frame header.
    /// </summary>
    /// <param name="type">The frame type.</param>
    /// <param name="payloadLength">The payload length in bytes.</param>
    public FrameHeader(FrameType type, int payloadLength)
    {
        Type = type;
        PayloadLength = payloadLength;
    }

    /// <summary>
    /// Writes this frame header to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to (must be at least 5 bytes).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(Span<byte> buffer)
    {
        buffer[0] = (byte)Type;
        StreamBinaryHelpers.WriteInt32(buffer.Slice(1), PayloadLength);
    }

    /// <summary>
    /// Reads a frame header from the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from (must be at least 5 bytes).</param>
    /// <returns>The parsed frame header.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FrameHeader Read(ReadOnlySpan<byte> buffer)
    {
        return new FrameHeader
        {
            Type = (FrameType)buffer[0],
            PayloadLength = StreamBinaryHelpers.ReadInt32(buffer.Slice(1))
        };
    }

    /// <summary>
    /// Attempts to read a frame header from the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <param name="header">The parsed header if successful.</param>
    /// <returns>True if the buffer contained enough data, false otherwise.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead(ReadOnlySpan<byte> buffer, out FrameHeader header)
    {
        if (buffer.Length < Size)
        {
            header = default;
            return false;
        }

        header = Read(buffer);
        return true;
    }

    /// <summary>
    /// Gets the total frame size (header + payload).
    /// </summary>
    public int TotalFrameSize => Size + PayloadLength;

    /// <inheritdoc />
    public override string ToString() => $"FrameHeader {{ Type = {Type}, PayloadLength = {PayloadLength} }}";
}
