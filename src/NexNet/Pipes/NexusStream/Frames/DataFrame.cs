using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame containing a chunk of binary data.
/// Wire format: [Sequence:4][Data:N]
/// </summary>
public readonly struct DataFrame : IWritableFrame
{
    /// <summary>
    /// The size of the fixed portion of the frame (sequence number).
    /// </summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// The sequence number of this data chunk.
    /// Sequence numbers are continuous across the stream's lifetime.
    /// </summary>
    public uint Sequence { get; init; }

    /// <summary>
    /// The data payload.
    /// </summary>
    public Memory<byte> Data { get; init; }

    /// <summary>
    /// Creates a new Data frame.
    /// </summary>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="data">The data payload.</param>
    public DataFrame(uint sequence, Memory<byte> data)
    {
        Sequence = sequence;
        Data = data;
    }

    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    public int GetPayloadSize() => HeaderSize + Data.Length;

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        // Sequence
        StreamBinaryHelpers.WriteUInt32(buffer, Sequence);

        // Data
        Data.Span.CopyTo(buffer.Slice(HeaderSize));

        return HeaderSize + Data.Length;
    }

    /// <summary>
    /// Reads a Data frame from the specified payload buffer.
    /// The returned frame holds a reference to data that must be copied before the next read.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <param name="dataBuffer">Buffer to copy data into (must be at least buffer.Length - HeaderSize).</param>
    /// <returns>The parsed Data frame with data copied to dataBuffer.</returns>
    public static DataFrame Read(ReadOnlySpan<byte> buffer, Memory<byte> dataBuffer)
    {
        // Sequence
        var sequence = StreamBinaryHelpers.ReadUInt32(buffer);

        // Data
        var dataLength = buffer.Length - HeaderSize;
        buffer.Slice(HeaderSize, dataLength).CopyTo(dataBuffer.Span);

        return new DataFrame
        {
            Sequence = sequence,
            Data = dataBuffer.Slice(0, dataLength)
        };
    }

    /// <summary>
    /// Reads a Data frame from the specified payload buffer without copying data.
    /// The returned data span is only valid until the next read operation.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="dataLength">The length of the data.</param>
    public static void ReadHeader(ReadOnlySpan<byte> buffer, out uint sequence, out int dataLength)
    {
        sequence = StreamBinaryHelpers.ReadUInt32(buffer);
        dataLength = buffer.Length - HeaderSize;
    }

    /// <inheritdoc />
    public override string ToString() => $"DataFrame {{ Sequence = {Sequence}, DataLength = {Data.Length} }}";
}
