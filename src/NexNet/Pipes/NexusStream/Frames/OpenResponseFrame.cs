using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Response frame sent after receiving an Open request.
/// Wire format:
/// - Success (1 byte): 1 if successful, 0 if failed
/// - If Success=true: Metadata (9 bytes)
/// - If Success=false: ErrorCode (4 bytes) + ErrorMessage (2+N bytes)
/// </summary>
public readonly struct OpenResponseFrame
{
    /// <summary>
    /// Whether the open request succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error code if the request failed. Zero if successful.
    /// </summary>
    public StreamErrorCode ErrorCode { get; init; }

    /// <summary>
    /// Error message if the request failed. Null or empty if successful.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Stream metadata if successful. Default if failed.
    /// </summary>
    public NexusStreamMetadata Metadata { get; init; }

    /// <summary>
    /// Creates a successful open response with the given metadata.
    /// </summary>
    /// <param name="metadata">The stream metadata.</param>
    public OpenResponseFrame(NexusStreamMetadata metadata)
    {
        Success = true;
        ErrorCode = StreamErrorCode.Success;
        ErrorMessage = null;
        Metadata = metadata;
    }

    /// <summary>
    /// Creates a failed open response with the given error information.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="errorMessage">The error message.</param>
    public OpenResponseFrame(StreamErrorCode errorCode, string? errorMessage)
    {
        Success = false;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Metadata = default;
    }

    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    public int GetPayloadSize()
    {
        // Success flag (1)
        if (Success)
        {
            // Success (1) + Metadata (9)
            return 1 + NexusStreamMetadata.Size;
        }
        else
        {
            // Success (1) + ErrorCode (4) + ErrorMessage (2 + N)
            return 1 + 4 + StreamBinaryHelpers.GetStringSize(ErrorMessage);
        }
    }

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        var offset = 0;

        // Success flag
        StreamBinaryHelpers.WriteBool(buffer, Success);
        offset += 1;

        if (Success)
        {
            // Metadata
            Metadata.Write(buffer.Slice(offset));
            offset += NexusStreamMetadata.Size;
        }
        else
        {
            // ErrorCode
            StreamBinaryHelpers.WriteInt32(buffer.Slice(offset), (int)ErrorCode);
            offset += 4;

            // ErrorMessage
            offset += StreamBinaryHelpers.WriteString(buffer.Slice(offset), ErrorMessage);
        }

        return offset;
    }

    /// <summary>
    /// Reads an OpenResponse frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed OpenResponse frame.</returns>
    public static OpenResponseFrame Read(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;

        // Success flag
        var success = StreamBinaryHelpers.ReadBool(buffer);
        offset += 1;

        if (success)
        {
            // Metadata
            var metadata = NexusStreamMetadata.Read(buffer.Slice(offset));
            return new OpenResponseFrame(metadata);
        }
        else
        {
            // ErrorCode
            var errorCode = (StreamErrorCode)StreamBinaryHelpers.ReadInt32(buffer.Slice(offset));
            offset += 4;

            // ErrorMessage
            var errorMessage = StreamBinaryHelpers.ReadString(buffer.Slice(offset), out _);

            return new OpenResponseFrame(errorCode, errorMessage);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (Success)
        {
            return $"OpenResponse {{ Success = true, Metadata = {Metadata} }}";
        }
        else
        {
            return $"OpenResponse {{ Success = false, ErrorCode = {ErrorCode}, ErrorMessage = \"{ErrorMessage}\" }}";
        }
    }
}
