using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame indicating the stream should be closed.
/// Wire format: [Graceful:1]
/// </summary>
public readonly struct CloseFrame : IWritableFrame
{
    /// <summary>
    /// The size of the payload in bytes.
    /// </summary>
    public const int PayloadSize = 1;

    /// <summary>
    /// True for a graceful close, false for an abrupt close.
    /// </summary>
    public bool Graceful { get; init; }

    /// <summary>
    /// Creates a new Close frame.
    /// </summary>
    /// <param name="graceful">True for graceful close.</param>
    public CloseFrame(bool graceful)
    {
        Graceful = graceful;
    }

    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    public int GetPayloadSize() => PayloadSize;

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        StreamBinaryHelpers.WriteBool(buffer, Graceful);
        return PayloadSize;
    }

    /// <summary>
    /// Reads a Close frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed Close frame.</returns>
    public static CloseFrame Read(ReadOnlySpan<byte> buffer)
    {
        return new CloseFrame
        {
            Graceful = StreamBinaryHelpers.ReadBool(buffer)
        };
    }

    /// <inheritdoc />
    public override string ToString() => $"CloseFrame {{ Graceful = {Graceful} }}";
}
