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
        if (payload.IsSingleSegment)
        {
            return OpenFrame.Read(payload.FirstSpan);
        }

        // Multi-segment: copy to contiguous buffer
        var buffer = ArrayPool<byte>.Shared.Rent((int)payload.Length);
        try
        {
            payload.CopyTo(buffer);
            return OpenFrame.Read(buffer.AsSpan(0, (int)payload.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses a Close frame from a payload sequence.
    /// </summary>
    public static CloseFrame ParseClose(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return CloseFrame.Read(payload.FirstSpan);
        }

        // Multi-segment: copy to contiguous buffer
        var buffer = ArrayPool<byte>.Shared.Rent((int)payload.Length);
        try
        {
            payload.CopyTo(buffer);
            return CloseFrame.Read(buffer.AsSpan(0, (int)payload.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Parses an Error frame from a payload sequence.
    /// </summary>
    public static ErrorFrame ParseError(ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
        {
            return ErrorFrame.Read(payload.FirstSpan);
        }

        // Multi-segment: copy to contiguous buffer
        var buffer = ArrayPool<byte>.Shared.Rent((int)payload.Length);
        try
        {
            payload.CopyTo(buffer);
            return ErrorFrame.Read(buffer.AsSpan(0, (int)payload.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}
