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
    public async ValueTask WriteOpenAsync(OpenFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Open, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a Close frame.
    /// </summary>
    public async ValueTask WriteCloseAsync(CloseFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Close, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes an Error frame.
    /// </summary>
    public async ValueTask WriteErrorAsync(ErrorFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Error, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes an OpenResponse frame.
    /// </summary>
    public async ValueTask WriteOpenResponseAsync(OpenResponseFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.OpenResponse, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
    public async ValueTask WriteReadAsync(ReadFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Read, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a Write frame.
    /// </summary>
    public async ValueTask WriteWriteAsync(WriteFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Write, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a WriteResponse frame.
    /// </summary>
    public async ValueTask WriteWriteResponseAsync(WriteResponseFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.WriteResponse, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a Data frame.
    /// </summary>
    public async ValueTask WriteDataAsync(DataFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.Data, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a DataEnd frame.
    /// </summary>
    public async ValueTask WriteDataEndAsync(DataEndFrame frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadSize = frame.GetPayloadSize();
            var totalSize = FrameHeader.Size + payloadSize;

            var buffer = _output.GetMemory(totalSize);

            // Write header
            var header = new FrameHeader(FrameType.DataEnd, payloadSize);
            header.Write(buffer.Span);

            // Write payload
            frame.Write(buffer.Span.Slice(FrameHeader.Size));

            _output.Advance(totalSize);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
}
