using System;
using System.Buffers.Binary;
using System.IO;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame requesting a seek operation on the stream.
/// </summary>
public readonly struct SeekFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes.
    /// </summary>
    public const int Size = 9; // 8 (offset) + 1 (origin)

    /// <summary>
    /// Gets the offset to seek to.
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// Gets the origin for the seek operation.
    /// </summary>
    public SeekOrigin Origin { get; init; }

    /// <summary>
    /// Creates a new seek frame.
    /// </summary>
    /// <param name="offset">The offset to seek to.</param>
    /// <param name="origin">The origin for the seek operation.</param>
    public SeekFrame(long offset, SeekOrigin origin)
    {
        Offset = offset;
        Origin = origin;
    }

    /// <summary>
    /// Gets the payload size.
    /// </summary>
    public int GetPayloadSize() => Size;

    /// <summary>
    /// Writes the frame to a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer, Offset);
        buffer[8] = (byte)Origin;
        return Size;
    }

    /// <summary>
    /// Reads a frame from a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <returns>The parsed frame.</returns>
    public static SeekFrame Read(ReadOnlySpan<byte> buffer)
    {
        var offset = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var origin = (SeekOrigin)buffer[8];
        return new SeekFrame(offset, origin);
    }

    /// <inheritdoc />
    public override string ToString() => $"SeekFrame(Offset={Offset}, Origin={Origin})";
}
