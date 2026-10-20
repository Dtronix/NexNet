using System;
using System.Buffers.Binary;

namespace NexNet.Pipes.NexusStream.Frames;

/// <summary>
/// Frame containing transfer progress information.
/// Sent in both directions to report transfer statistics.
/// </summary>
public readonly struct ProgressFrame : IWritableFrame
{
    /// <summary>
    /// Size of the frame payload in bytes.
    /// 8 (bytesTransferred) + 8 (totalBytes) + 8 (elapsedTicks) + 8 (bytesPerSecond) + 1 (state) = 33 bytes.
    /// </summary>
    public const int Size = 33;

    /// <summary>
    /// Gets the number of bytes transferred so far.
    /// </summary>
    public long BytesTransferred { get; init; }

    /// <summary>
    /// Gets the total bytes expected, or -1 if unknown.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Gets the elapsed time in ticks since the transfer started.
    /// </summary>
    public long ElapsedTicks { get; init; }

    /// <summary>
    /// Gets the current transfer rate in bytes per second.
    /// </summary>
    public double BytesPerSecond { get; init; }

    /// <summary>
    /// Gets the current state of the transfer.
    /// </summary>
    public TransferState State { get; init; }

    /// <summary>
    /// Creates a new progress frame.
    /// </summary>
    public ProgressFrame(long bytesTransferred, long totalBytes, long elapsedTicks, double bytesPerSecond, TransferState state)
    {
        BytesTransferred = bytesTransferred;
        TotalBytes = totalBytes;
        ElapsedTicks = elapsedTicks;
        BytesPerSecond = bytesPerSecond;
        State = state;
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
        BinaryPrimitives.WriteInt64LittleEndian(buffer, BytesTransferred);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(8), TotalBytes);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(16), ElapsedTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(24), BitConverter.DoubleToInt64Bits(BytesPerSecond));
        buffer[32] = (byte)State;
        return Size;
    }

    /// <summary>
    /// Reads a frame from a buffer.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <returns>The parsed frame.</returns>
    public static ProgressFrame Read(ReadOnlySpan<byte> buffer)
    {
        var bytesTransferred = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var totalBytes = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(8));
        var elapsedTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(16));
        var bytesPerSecond = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(24)));
        var state = (TransferState)buffer[32];

        return new ProgressFrame(bytesTransferred, totalBytes, elapsedTicks, bytesPerSecond, state);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"ProgressFrame(Transferred={BytesTransferred}/{TotalBytes}, Rate={BytesPerSecond:F0} B/s, State={State})";
}
