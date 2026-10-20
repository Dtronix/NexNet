using System;
using System.Buffers.Binary;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Response frame for a seek operation.
/// </summary>
public readonly struct SeekResponseFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes.
    /// </summary>
    public const int Size = 13; // 1 (success) + 8 (position) + 4 (errorCode)

    /// <summary>
    /// Gets whether the seek was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the resulting position after the seek (or current position on failure).
    /// </summary>
    public long Position { get; init; }

    /// <summary>
    /// Gets the error code if the seek failed.
    /// </summary>
    public StreamErrorCode ErrorCode { get; init; }

    /// <summary>
    /// Creates a successful seek response.
    /// </summary>
    /// <param name="position">The new position after seek.</param>
    public SeekResponseFrame(long position)
    {
        Success = true;
        Position = position;
        ErrorCode = StreamErrorCode.Success;
    }

    /// <summary>
    /// Creates a failed seek response.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="currentPosition">The current position for resync.</param>
    public SeekResponseFrame(StreamErrorCode errorCode, long currentPosition)
    {
        Success = false;
        Position = currentPosition;
        ErrorCode = errorCode;
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
        buffer[0] = Success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(1), Position);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(9), (int)ErrorCode);
        return Size;
    }

    /// <summary>
    /// Reads a frame from a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <returns>The parsed frame.</returns>
    public static SeekResponseFrame Read(ReadOnlySpan<byte> buffer)
    {
        var success = buffer[0] != 0;
        var position = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(1));
        var errorCode = (StreamErrorCode)BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(9));

        return new SeekResponseFrame
        {
            Success = success,
            Position = position,
            ErrorCode = errorCode
        };
    }

    /// <inheritdoc />
    public override string ToString() =>
        Success
            ? $"SeekResponseFrame(Success, Position={Position})"
            : $"SeekResponseFrame(Failed, ErrorCode={ErrorCode}, Position={Position})";
}
