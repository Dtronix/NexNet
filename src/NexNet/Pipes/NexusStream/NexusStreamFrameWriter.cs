using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream.Frames;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Writes NexStream protocol frames to a <see cref="PipeWriter"/>.
/// Thread-safe through async locking.
/// </summary>
internal sealed class NexusStreamFrameWriter
{
    /// <summary>
    /// Default maximum payload size for data frames (0.5 MB).
    /// </summary>
    public const int DefaultMaxPayloadSize = 524288;

    private readonly PipeWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _maxPayloadSize;

    /// <summary>
    /// Creates a new frame writer.
    /// </summary>
    /// <param name="output">The pipe writer to write frames to.</param>
    /// <param name="maxPayloadSize">Maximum payload size for data frames (default: 0.5 MB).</param>
    public NexusStreamFrameWriter(PipeWriter output, int maxPayloadSize = DefaultMaxPayloadSize)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _maxPayloadSize = maxPayloadSize;
    }

    /// <summary>
    /// Gets the maximum payload size for data frames.
    /// </summary>
    public int MaxPayloadSize => _maxPayloadSize;

    /// <summary>
    /// Internal helper that writes a typed frame without locking.
    /// Caller must hold the write lock.
    /// </summary>
    /// <typeparam name="TFrame">The frame type implementing IWritableFrame.</typeparam>
    /// <param name="type">The frame type enum value.</param>
    /// <param name="frame">The frame to write.</param>
    /// <param name="ct">Cancellation token.</param>
    private async ValueTask WriteFrameUnlockedAsync<TFrame>(FrameType type, TFrame frame, CancellationToken ct)
        where TFrame : struct, IWritableFrame
    {
        var payloadSize = frame.GetPayloadSize();
        var totalSize = FrameHeader.Size + payloadSize;

        var buffer = _output.GetMemory(totalSize);

        // Write header
        new FrameHeader(type, payloadSize).Write(buffer.Span);

        // Write payload
        if (payloadSize > 0)
        {
            frame.Write(buffer.Span.Slice(FrameHeader.Size));
        }

        _output.Advance(totalSize);
        await _output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a typed frame with locking.
    /// </summary>
    /// <typeparam name="TFrame">The frame type implementing IWritableFrame.</typeparam>
    /// <param name="type">The frame type enum value.</param>
    /// <param name="frame">The frame to write.</param>
    /// <param name="ct">Cancellation token.</param>
    private async ValueTask WriteFrameAsync<TFrame>(FrameType type, TFrame frame, CancellationToken ct)
        where TFrame : struct, IWritableFrame
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameUnlockedAsync(type, frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a raw frame with the specified type and payload.
    /// </summary>
    /// <param name="type">The frame type.</param>
    /// <param name="payload">The frame payload.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask WriteFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var header = new FrameHeader(type, payload.Length);
            var totalSize = FrameHeader.Size + payload.Length;

            var buffer = _output.GetMemory(totalSize);
            header.Write(buffer.Span);

            if (payload.Length > 0)
            {
                payload.Span.CopyTo(buffer.Span.Slice(FrameHeader.Size));
            }

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes an Open frame.
    /// </summary>
    public ValueTask WriteOpenAsync(OpenFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Open, frame, ct);

    /// <summary>
    /// Writes a Close frame.
    /// </summary>
    public ValueTask WriteCloseAsync(CloseFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Close, frame, ct);

    /// <summary>
    /// Writes an Error frame.
    /// </summary>
    public ValueTask WriteErrorAsync(ErrorFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Error, frame, ct);

    /// <summary>
    /// Writes an OpenResponse frame.
    /// </summary>
    public ValueTask WriteOpenResponseAsync(OpenResponseFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.OpenResponse, frame, ct);

    /// <summary>
    /// Writes a frame with an empty payload.
    /// </summary>
    public async ValueTask WriteEmptyFrameAsync(FrameType type, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var buffer = _output.GetMemory(FrameHeader.Size);

            var header = new FrameHeader(type, 0);
            header.Write(buffer.Span);

            _output.Advance(FrameHeader.Size);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a Read frame.
    /// </summary>
    public ValueTask WriteReadAsync(ReadFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Read, frame, ct);

    /// <summary>
    /// Writes a Write frame.
    /// </summary>
    public ValueTask WriteWriteAsync(WriteFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Write, frame, ct);

    /// <summary>
    /// Writes a WriteResponse frame.
    /// </summary>
    public ValueTask WriteWriteResponseAsync(WriteResponseFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.WriteResponse, frame, ct);

    /// <summary>
    /// Writes a Data frame.
    /// </summary>
    public ValueTask WriteDataAsync(DataFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Data, frame, ct);

    /// <summary>
    /// Writes a DataEnd frame.
    /// </summary>
    public ValueTask WriteDataEndAsync(DataEndFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.DataEnd, frame, ct);

    /// <summary>
    /// Chunks data into Data frames suitable for transmission.
    /// Each chunk is at most MaxPayloadSize - DataFrame.HeaderSize bytes.
    /// </summary>
    /// <param name="data">The data to chunk.</param>
    /// <param name="sequenceManager">The sequence manager for assigning sequence numbers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of Data frames.</returns>
    public async IAsyncEnumerable<DataFrame> ChunkDataAsync(
        ReadOnlyMemory<byte> data,
        SequenceManager sequenceManager,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Calculate max data per chunk (subtract header size from max payload)
        var maxDataPerChunk = _maxPayloadSize - DataFrame.HeaderSize;
        if (maxDataPerChunk <= 0)
        {
            throw new InvalidOperationException(
                $"MaxPayloadSize ({_maxPayloadSize}) is too small. Must be greater than {DataFrame.HeaderSize}.");
        }

        var offset = 0;
        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = data.Length - offset;
            var chunkSize = Math.Min(remaining, maxDataPerChunk);
            var chunkData = data.Slice(offset, chunkSize);

            // Create a mutable copy for the frame
            var chunk = new byte[chunkSize];
            chunkData.CopyTo(chunk);

            var frame = new DataFrame(sequenceManager.GetNextSendSequence(), chunk);
            yield return frame;

            offset += chunkSize;

            // Yield to allow other operations (simulates async source)
            await Task.Yield();
        }
    }

    /// <summary>
    /// Writes chunked data frames without acquiring write lock.
    /// Caller must hold the write lock.
    /// </summary>
    internal async ValueTask WriteDataChunksUnlockedAsync(
        ReadOnlyMemory<byte> data,
        SequenceManager sequenceManager,
        CancellationToken ct = default)
    {
        var maxDataPerChunk = _maxPayloadSize - DataFrame.HeaderSize;

        var offset = 0;
        while (offset < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = data.Length - offset;
            var chunkSize = Math.Min(remaining, maxDataPerChunk);
            var chunkData = data.Slice(offset, chunkSize);

            var sequence = sequenceManager.GetNextSendSequence();
            var payloadSize = DataFrame.HeaderSize + chunkSize;
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Data, payloadSize);
            header.Write(buffer.Span);

            // Write sequence
            StreamBinaryHelpers.WriteUInt32(buffer.Span.Slice(FrameHeader.Size), sequence);

            // Write data
            chunkData.Span.CopyTo(buffer.Span.Slice(FrameHeader.Size + DataFrame.HeaderSize));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);

            offset += chunkSize;
        }
    }

    /// <summary>
    /// Writes a Seek frame.
    /// </summary>
    public ValueTask WriteSeekAsync(SeekFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Seek, frame, ct);

    /// <summary>
    /// Writes a SeekResponse frame.
    /// </summary>
    public ValueTask WriteSeekResponseAsync(SeekResponseFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.SeekResponse, frame, ct);

    /// <summary>
    /// Writes a Flush frame.
    /// </summary>
    public ValueTask WriteFlushAsync(CancellationToken ct = default)
        => WriteFrameAsync(FrameType.Flush, new FlushFrame(), ct);

    /// <summary>
    /// Writes a FlushResponse frame.
    /// </summary>
    public ValueTask WriteFlushResponseAsync(FlushResponseFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.FlushResponse, frame, ct);

    /// <summary>
    /// Writes a SetLength frame.
    /// </summary>
    public ValueTask WriteSetLengthAsync(SetLengthFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.SetLength, frame, ct);

    /// <summary>
    /// Writes a SetLengthResponse frame.
    /// </summary>
    public ValueTask WriteSetLengthResponseAsync(SetLengthResponseFrame frame, CancellationToken ct = default)
        => WriteFrameAsync(FrameType.SetLengthResponse, frame, ct);
}
