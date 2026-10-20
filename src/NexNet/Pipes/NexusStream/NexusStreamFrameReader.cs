using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream.Frames;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Reads NexStream protocol frames from a <see cref="PipeReader"/>.
/// The returned payload is valid until the next read operation.
/// </summary>
internal sealed class NexusStreamFrameReader
{
    private readonly PipeReader _input;
    private SequencePosition? _pendingAdvanceTo;

    /// <summary>
    /// Creates a new frame reader.
    /// </summary>
    /// <param name="input">The pipe reader to read frames from.</param>
    public NexusStreamFrameReader(PipeReader input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>
    /// Reads the next frame from the pipe.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The frame header and payload, or null if the pipe is completed.
    /// The payload is valid until the next call to ReadFrameAsync or TryReadFrame.
    /// </returns>
    public async ValueTask<(FrameHeader Header, ReadOnlySequence<byte> Payload)?> ReadFrameAsync(CancellationToken ct = default)
    {
        // Advance past the previous frame if needed
        if (_pendingAdvanceTo.HasValue)
        {
            _input.AdvanceTo(_pendingAdvanceTo.Value);
            _pendingAdvanceTo = null;
        }

        while (true)
        {
            var result = await _input.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            // Check if we have enough data for the header
            if (buffer.Length < FrameHeader.Size)
            {
                if (result.IsCompleted)
                {
                    // Pipe completed without enough data
                    _input.AdvanceTo(buffer.End);
                    return null;
                }

                // Need more data
                _input.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            // Read the header
            var header = ReadHeader(buffer);

            // Check if we have the full frame
            var totalFrameSize = FrameHeader.Size + header.PayloadLength;
            if (buffer.Length < totalFrameSize)
            {
                if (result.IsCompleted)
                {
                    // Pipe completed with incomplete frame
                    throw new InvalidDataException($"Incomplete frame: expected {totalFrameSize} bytes, got {buffer.Length}");
                }

                // Need more data
                _input.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            // Extract the payload (valid until next read)
            var payload = buffer.Slice(FrameHeader.Size, header.PayloadLength);

            // Store position for deferred advance
            _pendingAdvanceTo = buffer.GetPosition(totalFrameSize);

            return (header, payload);
        }
    }

    /// <summary>
    /// Attempts to read a frame synchronously if enough data is buffered.
    /// </summary>
    /// <param name="header">The frame header if successful.</param>
    /// <param name="payload">The frame payload if successful, valid until next read.</param>
    /// <returns>True if a frame was read, false if more data is needed.</returns>
    public bool TryReadFrame(out FrameHeader header, out ReadOnlySequence<byte> payload)
    {
        // Advance past the previous frame if needed
        if (_pendingAdvanceTo.HasValue)
        {
            _input.AdvanceTo(_pendingAdvanceTo.Value);
            _pendingAdvanceTo = null;
        }

        if (!_input.TryRead(out var result))
        {
            header = default;
            payload = default;
            return false;
        }

        var buffer = result.Buffer;

        // Check if we have enough data for the header
        if (buffer.Length < FrameHeader.Size)
        {
            _input.AdvanceTo(buffer.Start, buffer.End);
            header = default;
            payload = default;
            return false;
        }

        // Read the header
        header = ReadHeader(buffer);

        // Check if we have the full frame
        var totalFrameSize = FrameHeader.Size + header.PayloadLength;
        if (buffer.Length < totalFrameSize)
        {
            _input.AdvanceTo(buffer.Start, buffer.End);
            payload = default;
            return false;
        }

        // Extract the payload (valid until next read)
        payload = buffer.Slice(FrameHeader.Size, header.PayloadLength);

        // Store position for deferred advance
        _pendingAdvanceTo = buffer.GetPosition(totalFrameSize);

        return true;
    }

    private static FrameHeader ReadHeader(ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            return FrameHeader.Read(buffer.FirstSpan);
        }

        // Multi-segment: copy header bytes to stack
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        buffer.Slice(0, FrameHeader.Size).CopyTo(headerBytes);
        return FrameHeader.Read(headerBytes);
    }

    // =============================================
    // Static Frame Parsing Methods
    // =============================================

    /// <summary>
    /// Parses an Open frame from a payload sequence.
    /// </summary>
    public static OpenFrame ParseOpen(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return OpenFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a Close frame from a payload sequence.
    /// </summary>
    public static CloseFrame ParseClose(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return CloseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses an Error frame from a payload sequence.
    /// </summary>
    public static ErrorFrame ParseError(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return ErrorFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses an OpenResponse frame from a payload sequence.
    /// </summary>
    public static OpenResponseFrame ParseOpenResponse(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return OpenResponseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a Read frame from a payload sequence.
    /// </summary>
    public static ReadFrame ParseRead(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return ReadFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a Write frame from a payload sequence.
    /// </summary>
    public static WriteFrame ParseWrite(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return WriteFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a WriteResponse frame from a payload sequence.
    /// </summary>
    public static WriteResponseFrame ParseWriteResponse(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return WriteResponseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a Data frame from a payload sequence, copying data to the provided buffer.
    /// </summary>
    /// <param name="payload">The payload sequence.</param>
    /// <param name="dataBuffer">Buffer to copy data into.</param>
    /// <returns>The parsed Data frame with data in the provided buffer.</returns>
    public static DataFrame ParseData(ReadOnlySequence<byte> payload, Memory<byte> dataBuffer)
    {
        using var buffer = new ContiguousBuffer(payload);
        return DataFrame.Read(buffer.Span, dataBuffer);
    }

    /// <summary>
    /// Parses a Data frame header without copying data.
    /// Use this when you need to validate sequence before copying.
    /// </summary>
    /// <param name="payload">The payload sequence.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <param name="dataLength">The length of the data.</param>
    public static void ParseDataHeader(ReadOnlySequence<byte> payload, out uint sequence, out int dataLength)
    {
        using var buffer = new ContiguousBuffer(payload);
        DataFrame.ReadHeader(buffer.Span, out sequence, out dataLength);
    }

    /// <summary>
    /// Parses a DataEnd frame from a payload sequence.
    /// </summary>
    public static DataEndFrame ParseDataEnd(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return DataEndFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a Seek frame from a payload sequence.
    /// </summary>
    public static SeekFrame ParseSeek(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return SeekFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a SeekResponse frame from a payload sequence.
    /// </summary>
    public static SeekResponseFrame ParseSeekResponse(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return SeekResponseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a FlushResponse frame from a payload sequence.
    /// </summary>
    public static FlushResponseFrame ParseFlushResponse(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return FlushResponseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a SetLength frame from a payload sequence.
    /// </summary>
    public static SetLengthFrame ParseSetLength(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return SetLengthFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a SetLengthResponse frame from a payload sequence.
    /// </summary>
    public static SetLengthResponseFrame ParseSetLengthResponse(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return SetLengthResponseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a MetadataResponse frame from a payload sequence.
    /// </summary>
    public static MetadataResponseFrame ParseMetadataResponse(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return MetadataResponseFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses a Progress frame from a payload sequence.
    /// </summary>
    public static ProgressFrame ParseProgress(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return ProgressFrame.Read(buffer.Span);
    }

    /// <summary>
    /// Parses an Ack frame from a payload sequence.
    /// </summary>
    public static AckFrame ParseAck(ReadOnlySequence<byte> payload)
    {
        using var buffer = new ContiguousBuffer(payload);
        return AckFrame.Read(buffer.Span);
    }

    /// <summary>
    /// A ref struct that provides a contiguous span from a ReadOnlySequence,
    /// renting from ArrayPool if the sequence has multiple segments.
    /// </summary>
    private ref struct ContiguousBuffer
    {
        private readonly ReadOnlySpan<byte> _span;
        private readonly byte[]? _rented;

        public ContiguousBuffer(ReadOnlySequence<byte> sequence)
        {
            if (sequence.IsSingleSegment)
            {
                _span = sequence.FirstSpan;
                _rented = null;
            }
            else
            {
                _rented = ArrayPool<byte>.Shared.Rent((int)sequence.Length);
                sequence.CopyTo(_rented);
                _span = _rented.AsSpan(0, (int)sequence.Length);
            }
        }

        public ReadOnlySpan<byte> Span => _span;

        public void Dispose()
        {
            if (_rented != null)
            {
                ArrayPool<byte>.Shared.Return(_rented);
            }
        }
    }
}
