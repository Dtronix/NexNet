using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Response frame containing stream metadata.
/// </summary>
public readonly struct MetadataResponseFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes.
    /// </summary>
    public const int Size = NexusStreamMetadata.Size;

    /// <summary>
    /// Gets the stream metadata.
    /// </summary>
    public NexusStreamMetadata Metadata { get; init; }

    /// <summary>
    /// Creates a new metadata response frame.
    /// </summary>
    /// <param name="metadata">The stream metadata.</param>
    public MetadataResponseFrame(NexusStreamMetadata metadata)
    {
        Metadata = metadata;
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
        return Metadata.Write(buffer);
    }

    /// <summary>
    /// Reads a frame from a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <returns>The parsed frame.</returns>
    public static MetadataResponseFrame Read(ReadOnlySpan<byte> buffer)
    {
        return new MetadataResponseFrame(NexusStreamMetadata.Read(buffer));
    }

    /// <inheritdoc />
    public override string ToString() => $"MetadataResponseFrame({Metadata})";
}
