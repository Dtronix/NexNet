using System;
using System.IO;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame requesting to open a stream for a resource.
/// Wire format: [ResourceId:2+N][Access:1][Share:1][ResumePosition:8]
/// </summary>
public readonly struct OpenFrame
{
    /// <summary>
    /// The resource identifier (max 2000 characters).
    /// </summary>
    public string ResourceId { get; init; }

    /// <summary>
    /// The requested access mode.
    /// </summary>
    public StreamAccessMode Access { get; init; }

    /// <summary>
    /// The requested sharing mode.
    /// </summary>
    public StreamShareMode Share { get; init; }

    /// <summary>
    /// Position to resume from, or -1 for a fresh start.
    /// </summary>
    public long ResumePosition { get; init; }

    /// <summary>
    /// Creates a new Open frame.
    /// </summary>
    public OpenFrame(string resourceId, StreamAccessMode access, StreamShareMode share = StreamShareMode.None, long resumePosition = -1)
    {
        if (resourceId == null)
            throw new ArgumentNullException(nameof(resourceId));
        if (resourceId.Length > StreamBinaryHelpers.MaxResourceIdLength)
            throw new ArgumentException($"Resource ID exceeds maximum length of {StreamBinaryHelpers.MaxResourceIdLength} characters.", nameof(resourceId));

        ResourceId = resourceId;
        Access = access;
        Share = share;
        ResumePosition = resumePosition;
    }

    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    public int GetPayloadSize()
    {
        // ResourceId (2-byte length + UTF-8) + Access (1) + Share (1) + ResumePosition (8)
        return StreamBinaryHelpers.GetStringSize(ResourceId) + 1 + 1 + 8;
    }

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        var offset = 0;

        // ResourceId
        offset += StreamBinaryHelpers.WriteString(buffer, ResourceId);

        // Access
        buffer[offset++] = (byte)Access;

        // Share
        buffer[offset++] = (byte)Share;

        // ResumePosition
        StreamBinaryHelpers.WriteInt64(buffer.Slice(offset), ResumePosition);
        offset += 8;

        return offset;
    }

    /// <summary>
    /// Reads an Open frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed Open frame.</returns>
    public static OpenFrame Read(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;

        // ResourceId
        var resourceId = StreamBinaryHelpers.ReadString(buffer, out var bytesRead);
        offset += bytesRead;

        if (resourceId == null)
            throw new InvalidDataException("ResourceId cannot be null in Open frame.");

        // Access
        var access = (StreamAccessMode)buffer[offset++];

        // Share
        var share = (StreamShareMode)buffer[offset++];

        // ResumePosition
        var resumePosition = StreamBinaryHelpers.ReadInt64(buffer.Slice(offset));

        return new OpenFrame
        {
            ResourceId = resourceId,
            Access = access,
            Share = share,
            ResumePosition = resumePosition
        };
    }

    /// <inheritdoc />
    public override string ToString() => $"OpenFrame {{ ResourceId = \"{ResourceId}\", Access = {Access}, Share = {Share}, ResumePosition = {ResumePosition} }}";
}
