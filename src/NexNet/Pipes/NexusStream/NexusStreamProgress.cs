using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Progress information for an ongoing stream transfer.
/// </summary>
public readonly struct NexusStreamProgress
{
    /// <summary>
    /// Total bytes read so far.
    /// </summary>
    public long BytesRead { get; init; }

    /// <summary>
    /// Total bytes written so far.
    /// </summary>
    public long BytesWritten { get; init; }

    /// <summary>
    /// Expected total bytes to read, or -1 if unknown.
    /// </summary>
    public long TotalReadBytes { get; init; }

    /// <summary>
    /// Expected total bytes to write, or -1 if unknown.
    /// </summary>
    public long TotalWriteBytes { get; init; }

    /// <summary>
    /// Time elapsed since the transfer started.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Current read throughput in bytes per second.
    /// </summary>
    public double ReadBytesPerSecond { get; init; }

    /// <summary>
    /// Current write throughput in bytes per second.
    /// </summary>
    public double WriteBytesPerSecond { get; init; }

    /// <summary>
    /// Current state of the transfer.
    /// </summary>
    public TransferState State { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"Progress {{ Read = {BytesRead}/{TotalReadBytes}, Written = {BytesWritten}/{TotalWriteBytes}, State = {State} }}";
}
