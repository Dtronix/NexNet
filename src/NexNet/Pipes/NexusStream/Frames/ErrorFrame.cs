using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame indicating a stream error occurred.
/// Wire format: [ErrorCode:4][Position:8][Message:2+N]
/// </summary>
public readonly struct ErrorFrame
{
    /// <summary>
    /// The error code indicating what went wrong.
    /// </summary>
    public StreamErrorCode ErrorCode { get; init; }

    /// <summary>
    /// The stream position at which the error occurred.
    /// </summary>
    public long Position { get; init; }

    /// <summary>
    /// A human-readable error message.
    /// </summary>
    public string Message { get; init; }

    /// <summary>
    /// Creates a new Error frame.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="position">The stream position at error.</param>
    /// <param name="message">The error message.</param>
    public ErrorFrame(StreamErrorCode errorCode, long position, string message)
    {
        ErrorCode = errorCode;
        Position = position;
        Message = message ?? string.Empty;
    }

    /// <summary>
    /// Gets the size of the payload when serialized.
    /// </summary>
    public int GetPayloadSize()
    {
        // ErrorCode (4) + Position (8) + Message (2-byte length + UTF-8)
        return 4 + 8 + StreamBinaryHelpers.GetStringSize(Message);
    }

    /// <summary>
    /// Writes this frame's payload to the specified buffer.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <returns>The number of bytes written.</returns>
    public int Write(Span<byte> buffer)
    {
        var offset = 0;

        // ErrorCode
        StreamBinaryHelpers.WriteInt32(buffer, (int)ErrorCode);
        offset += 4;

        // Position
        StreamBinaryHelpers.WriteInt64(buffer.Slice(offset), Position);
        offset += 8;

        // Message
        offset += StreamBinaryHelpers.WriteString(buffer.Slice(offset), Message);

        return offset;
    }

    /// <summary>
    /// Reads an Error frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed Error frame.</returns>
    public static ErrorFrame Read(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;

        // ErrorCode
        var errorCode = (StreamErrorCode)StreamBinaryHelpers.ReadInt32(buffer);
        offset += 4;

        // Position
        var position = StreamBinaryHelpers.ReadInt64(buffer.Slice(offset));
        offset += 8;

        // Message
        var message = StreamBinaryHelpers.ReadString(buffer.Slice(offset), out _);

        return new ErrorFrame
        {
            ErrorCode = errorCode,
            Position = position,
            Message = message ?? string.Empty
        };
    }

    /// <summary>
    /// Returns true if this error code is a protocol error (100+) that requires disconnection.
    /// </summary>
    public bool IsProtocolError => (int)ErrorCode >= 100;

    /// <inheritdoc />
    public override string ToString() => $"ErrorFrame {{ ErrorCode = {ErrorCode}, Position = {Position}, Message = \"{Message}\" }}";
}
