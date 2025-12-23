using System;
using System.Buffers;
using System.IO.Pipelines;
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
    private readonly PipeWriter _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly int _maxPayloadSize;

    /// <summary>
    /// Creates a new frame writer.
    /// </summary>
    /// <param name="output">The pipe writer to write frames to.</param>
    /// <param name="maxPayloadSize">Maximum payload size for data frames (default: 65536).</param>
    public NexusStreamFrameWriter(PipeWriter output, int maxPayloadSize = 65536)
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
}
