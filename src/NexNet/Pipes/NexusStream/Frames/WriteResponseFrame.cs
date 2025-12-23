using System;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame acknowledging write completion.
/// Wire format: [Success:1][BytesWritten:4][Position:8][ErrorCode:4]
/// </summary>
public readonly struct WriteResponseFrame : IWritableFrame
{
    /// <summary>
    /// The wire size of this frame's payload.
    /// </summary>
    public const int Size = 1 + 4 + 8 + 4; // 17 bytes

    /// <summary>
    /// Whether the write was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The number of bytes actually written.
    /// </summary>
    public int BytesWritten { get; init; }

    /// <summary>
    /// The stream position after the write completed.
    /// </summary>
    public long Position { get; init; }

    /// <summary>
    /// Error code if the write failed (0 if successful).
    /// </summary>
    public StreamErrorCode ErrorCode { get; init; }

    /// <summary>
    /// Creates a successful write response.
    /// </summary>
    /// <param name="bytesWritten">The number of bytes written.</param>
    /// <param name="position">The position after write completion.</param>
    public WriteResponseFrame(int bytesWritten, long position)
    {
        Success = true;
        BytesWritten = bytesWritten;
        Position = position;
        ErrorCode = StreamErrorCode.Success;
    }

    /// <summary>
    /// Creates a failed write response.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="position">The current position (may be unchanged from before write).</param>
    public WriteResponseFrame(StreamErrorCode errorCode, long position = 0)
    {
        Success = false;
        BytesWritten = 0;
        Position = position;
        ErrorCode = errorCode;
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

        // Success
        StreamBinaryHelpers.WriteBool(buffer.Slice(offset), Success);
        offset += 1;

        // BytesWritten
        StreamBinaryHelpers.WriteInt32(buffer.Slice(offset), BytesWritten);
        offset += 4;

        // Position
        StreamBinaryHelpers.WriteInt64(buffer.Slice(offset), Position);
        offset += 8;

        // ErrorCode
        StreamBinaryHelpers.WriteInt32(buffer.Slice(offset), (int)ErrorCode);
        offset += 4;

        return offset;
    }

    /// <summary>
    /// Reads a WriteResponse frame from the specified payload buffer.
    /// </summary>
    /// <param name="buffer">The payload buffer to read from.</param>
    /// <returns>The parsed WriteResponse frame.</returns>
    public static WriteResponseFrame Read(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;

        // Success
        var success = StreamBinaryHelpers.ReadBool(buffer.Slice(offset));
        offset += 1;

        // BytesWritten
        var bytesWritten = StreamBinaryHelpers.ReadInt32(buffer.Slice(offset));
        offset += 4;

        // Position
        var position = StreamBinaryHelpers.ReadInt64(buffer.Slice(offset));
        offset += 8;

        // ErrorCode
        var errorCode = (StreamErrorCode)StreamBinaryHelpers.ReadInt32(buffer.Slice(offset));

        return new WriteResponseFrame
        {
            Success = success,
            BytesWritten = bytesWritten,
            Position = position,
            ErrorCode = errorCode
        };
    }

    /// <inheritdoc />
    public override string ToString() =>
        Success
            ? $"WriteResponseFrame {{ Success = true, BytesWritten = {BytesWritten}, Position = {Position} }}"
            : $"WriteResponseFrame {{ Success = false, ErrorCode = {ErrorCode}, Position = {Position} }}";
}
