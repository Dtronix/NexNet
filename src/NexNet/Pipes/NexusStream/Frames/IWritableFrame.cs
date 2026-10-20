using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Interface for frames that can be serialized to a buffer.
/// Used to reduce code duplication in frame writing.
/// </summary>
internal interface IWritableFrame
{
    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    /// <returns>The payload size in bytes.</returns>
    int GetPayloadSize();

    /// <summary>
    /// Writes the frame payload to the buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    int Write(Span<byte> buffer);
}
