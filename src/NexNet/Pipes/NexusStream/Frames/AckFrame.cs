using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame for acknowledging received data and updating flow control window.
/// Wire format: [AckedSequence:4][WindowSize:4]
/// </summary>
public readonly struct AckFrame : IWritableFrame
{
    /// <summary>
    /// The size of the frame payload in bytes.
    /// </summary>
    public const int Size = 8;

    /// <summary>
    /// Gets the sequence number being acknowledged (all sequences up to and including this are acknowledged).
    /// </summary>
    public uint AckedSequence { get; init; }

    /// <summary>
    /// Gets the receiver's available window size in bytes.
    /// </summary>
    public uint WindowSize { get; init; }

    /// <summary>
    /// Creates a new Ack frame.
    /// </summary>
    /// <param name="ackedSequence">The sequence number being acknowledged.</param>
    /// <param name="windowSize">The receiver's available window size.</param>
    public AckFrame(uint ackedSequence, uint windowSize)
    {
        AckedSequence = ackedSequence;
        WindowSize = windowSize;
    }

    /// <inheritdoc />
    public int GetPayloadSize() => Size;

    /// <inheritdoc />
    public int Write(Span<byte> buffer)
    {
        StreamBinaryHelpers.WriteUInt32(buffer, AckedSequence);
        StreamBinaryHelpers.WriteUInt32(buffer.Slice(4), WindowSize);
        return Size;
    }

    /// <summary>
    /// Reads an Ack frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed Ack frame.</returns>
    public static AckFrame Read(ReadOnlySpan<byte> buffer)
    {
        var ackedSequence = StreamBinaryHelpers.ReadUInt32(buffer);
        var windowSize = StreamBinaryHelpers.ReadUInt32(buffer.Slice(4));

        return new AckFrame(ackedSequence, windowSize);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"AckFrame {{ AckedSequence = {AckedSequence}, WindowSize = {WindowSize} }}";
}
