using System;
using System.Buffers.Binary;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame requesting to set the stream length.
/// </summary>
public readonly struct SetLengthFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes.
    /// </summary>
    public const int Size = 8;

    /// <summary>
    /// Gets the new length for the stream.
    /// </summary>
    public long Length { get; init; }

    /// <summary>
    /// Creates a new set length frame.
    /// </summary>
    /// <param name="length">The new length.</param>
    public SetLengthFrame(long length)
    {
        Length = length;
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
        BinaryPrimitives.WriteInt64LittleEndian(buffer, Length);
        return Size;
    }

    /// <summary>
    /// Reads a frame from a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <returns>The parsed frame.</returns>
    public static SetLengthFrame Read(ReadOnlySpan<byte> buffer)
    {
        var length = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        return new SetLengthFrame(length);
    }

    /// <inheritdoc />
    public override string ToString() => $"SetLengthFrame(Length={Length})";
}
