using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame marking the end of a data sequence.
/// Wire format: [TotalBytes:4][FinalSequence:4]
/// </summary>
public readonly struct DataEndFrame : IWritableFrame
{
    /// <summary>
    /// The wire size of this frame's payload.
    /// </summary>
    public const int Size = 4 + 4; // 8 bytes

    /// <summary>
    /// The total number of bytes transferred in this operation.
    /// </summary>
    public int TotalBytes { get; init; }

    /// <summary>
    /// The sequence number of the final Data frame.
    /// </summary>
    public uint FinalSequence { get; init; }

    /// <summary>
    /// Creates a new DataEnd frame.
    /// </summary>
    /// <param name="totalBytes">The total bytes transferred.</param>
    /// <param name="finalSequence">The final sequence number.</param>
    public DataEndFrame(int totalBytes, uint finalSequence)
    {
        TotalBytes = totalBytes;
        FinalSequence = finalSequence;
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
        var offset = 0;

        // TotalBytes
        StreamBinaryHelpers.WriteInt32(buffer.Slice(offset), TotalBytes);
        offset += 4;

        // FinalSequence
        StreamBinaryHelpers.WriteUInt32(buffer.Slice(offset), FinalSequence);
        offset += 4;

        return offset;
    }

    /// <summary>
    /// Reads a DataEnd frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed DataEnd frame.</returns>
    public static DataEndFrame Read(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;

        // TotalBytes
        var totalBytes = StreamBinaryHelpers.ReadInt32(buffer.Slice(offset));
        offset += 4;

        // FinalSequence
        var finalSequence = StreamBinaryHelpers.ReadUInt32(buffer.Slice(offset));

        return new DataEndFrame
        {
            TotalBytes = totalBytes,
            FinalSequence = finalSequence
        };
    }

    /// <inheritdoc />
    public override string ToString() => $"DataEndFrame {{ TotalBytes = {TotalBytes}, FinalSequence = {FinalSequence} }}";
}
