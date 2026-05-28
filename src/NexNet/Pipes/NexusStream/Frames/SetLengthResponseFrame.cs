using System;
using System.Buffers.Binary;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Response frame for a set length operation.
/// </summary>
public readonly struct SetLengthResponseFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes.
    /// </summary>
    public const int Size = 21; // 1 (success) + 8 (newLength) + 8 (position) + 4 (errorCode)

    /// <summary>
    /// Gets whether the set length was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the new length of the stream (or current length on failure).
    /// </summary>
    public long NewLength { get; init; }

    /// <summary>
    /// Gets the current position (for resync, may be adjusted if beyond new length).
    /// </summary>
    public long Position { get; init; }

    /// <summary>
    /// Gets the error code if the operation failed.
    /// </summary>
    public StreamErrorCode ErrorCode { get; init; }

    /// <summary>
    /// Creates a successful set length response.
    /// </summary>
    /// <param name="newLength">The new length of the stream.</param>
    /// <param name="position">The current position (may be adjusted).</param>
    public SetLengthResponseFrame(long newLength, long position)
    {
        Success = true;
        NewLength = newLength;
        Position = position;
        ErrorCode = StreamErrorCode.Success;
    }

    /// <summary>
    /// Creates a failed set length response.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="currentLength">The current length for resync.</param>
    /// <param name="currentPosition">The current position for resync.</param>
    public SetLengthResponseFrame(StreamErrorCode errorCode, long currentLength, long currentPosition)
    {
        Success = false;
        NewLength = currentLength;
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
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(1), NewLength);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(9), Position);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(17), (int)ErrorCode);
        return Size;
    }

    /// <summary>
    /// Reads a frame from a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <returns>The parsed frame.</returns>
    public static SetLengthResponseFrame Read(ReadOnlySpan<byte> buffer)
    {
        var success = buffer[0] != 0;
        var newLength = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(1));
        var position = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(9));
        var errorCode = (StreamErrorCode)BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(17));

        return new SetLengthResponseFrame
        {
            Success = success,
            NewLength = newLength,
            Position = position,
            ErrorCode = errorCode
        };
    }

    /// <inheritdoc />
    public override string ToString() =>
        Success
            ? $"SetLengthResponseFrame(Success, NewLength={NewLength}, Position={Position})"
            : $"SetLengthResponseFrame(Failed, ErrorCode={ErrorCode}, Length={NewLength}, Position={Position})";
}
