using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame requesting a flush operation on the stream.
/// This frame has no payload.
/// </summary>
public readonly struct FlushFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes (always 0).
    /// </summary>
    public const int Size = 0;

    /// <summary>
    /// Gets the payload size.
    /// </summary>
    public int GetPayloadSize() => Size;

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to (unused, payload is empty).</param>
    /// <returns>The number of bytes written (always 0).</returns>
    public int Write(Span<byte> buffer) => 0;

    /// <inheritdoc />
    public override string ToString() => "FlushFrame()";
}
