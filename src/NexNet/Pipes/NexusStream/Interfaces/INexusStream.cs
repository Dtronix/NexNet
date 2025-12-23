using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// A bidirectional stream over a NexNet duplex pipe.
/// </summary>
public interface INexusStream : IAsyncDisposable
{
    /// <summary>
    /// Gets the current state of the stream.
    /// </summary>
    NexusStreamState State { get; }

    /// <summary>
    /// Gets the error that caused the stream to fail, or null if no error occurred.
    /// </summary>
    Exception? Error { get; }

    /// <summary>
    /// Gets the current position within the stream.
    /// Throws <see cref="NotSupportedException"/> if the stream does not support seeking.
    /// </summary>
    long Position { get; }

    /// <summary>
    /// Gets the length of the stream in bytes, or -1 if unknown.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Gets whether the stream has a known length.
    /// </summary>
    bool HasKnownLength { get; }

    /// <summary>
    /// Gets whether the stream supports seeking.
    /// </summary>
    bool CanSeek { get; }

    /// <summary>
    /// Gets whether the stream supports reading.
    /// </summary>
    bool CanRead { get; }

    /// <summary>
    /// Gets whether the stream supports writing.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Reads data from the stream into the buffer.
    /// Returns when at least one byte is available, or zero at end of stream.
    /// </summary>
    /// <param name="buffer">The buffer to read data into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes read, or zero if end of stream.</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// Writes data from the buffer to the stream.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Seeks to a new position within the stream.
    /// </summary>
    /// <param name="offset">The offset relative to the origin.</param>
    /// <param name="origin">The reference point for the offset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new position within the stream.</returns>
    ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Flushes any buffered data to the underlying resource.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the length of the stream. Can truncate or extend the stream.
    /// </summary>
    /// <param name="length">The new length.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SetLengthAsync(long length, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata about the stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stream metadata.</returns>
    ValueTask<NexusStreamMetadata> GetMetadataAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets an observable that emits progress updates during transfers.
    /// </summary>
    IObservable<NexusStreamProgress> Progress { get; }

    /// <summary>
    /// Gets a <see cref="System.IO.Stream"/> wrapper for this stream.
    /// </summary>
    /// <returns>A Stream wrapper.</returns>
    Stream GetStream();
}
