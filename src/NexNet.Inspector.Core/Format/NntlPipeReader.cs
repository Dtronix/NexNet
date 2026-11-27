using System.Buffers;
using System.IO.Pipelines;

namespace NexNet.Inspector.Core.Format;

/// <summary>
/// Reads <c>.nntl</c> blocks in order from a forward-only stream: a live tap stream from an inspector endpoint, which is
/// a <c>.nntl</c> file being written (header, schema, session, snapshot, transport op, gap and session-close blocks,
/// then the index footer and trailer when the stream ends cleanly).
/// </summary>
/// <remarks>
/// Bytes are consumed from the pipe as they arrive and a block is assembled in the reader's own buffer, so a block
/// larger than a flow-controlled pipe's window (which only grants credit for consumed bytes) still completes. Each
/// block's checksum is verified. A live stream has no damage to resynchronize over, so a bad block throws rather than
/// being skipped. Not thread-safe.
/// </remarks>
public sealed class NntlPipeReader : IDisposable
{
    private readonly PipeReader _input;
    private readonly byte[] _header = new byte[NntlFormat.BlockHeaderSize];
    private byte[] _body = ArrayPool<byte>.Shared.Rent(4096);
    private bool _afterIndex;
    private bool _ended;

    /// <summary>Creates a reader over <paramref name="input"/>.</summary>
    /// <param name="input">The stream's reader; the first byte read must start the header block.</param>
    public NntlPipeReader(PipeReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
    }

    /// <summary>The block read last. Its body is owned by the reader and valid until the next read.</summary>
    public NntlBlock Current { get; private set; }

    /// <summary>Offset of the next block, counted from the first byte of the stream.</summary>
    public long Position { get; private set; }

    /// <summary>True once the stream ended with the index footer and trailer: the producer closed it cleanly.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Reads the next block into <see cref="Current"/>.</summary>
    /// <returns>False when the stream has ended: after the trailer, or when the producer stopped without one.</returns>
    /// <exception cref="NntlFormatException">The bytes are not a valid block, or the stream ends inside one.</exception>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_ended)
            return false;

        var fixedLength = _afterIndex ? NntlFormat.TrailerSize : NntlFormat.BlockHeaderSize;
        if (!await FillAsync(_header.AsMemory(0, fixedLength), cancellationToken).ConfigureAwait(false))
            return false;

        if (_afterIndex)
        {
            if (!NntlFormat.TryParseTrailerOffset(_header, out _))
                throw new NntlFormatException($"The stream does not end with a valid trailer at offset {Position}.");
            Position += fixedLength;
            IsComplete = true;
            _ended = true;
            return false;
        }

        if (!NntlFormat.TryParseBlockHeader(_header, out var kind, out var bodyLength))
            throw new NntlFormatException($"No valid block header at offset {Position}.");

        if (_body.Length < bodyLength)
        {
            ArrayPool<byte>.Shared.Return(_body);
            _body = ArrayPool<byte>.Shared.Rent(bodyLength);
        }

        var body = _body.AsMemory(0, bodyLength);
        if (!await FillAsync(body, cancellationToken).ConfigureAwait(false))
            throw new NntlFormatException($"The stream ended inside the block at offset {Position}.");
        if (!NntlFormat.IsChecksumValid(_header, body.Span))
            throw new NntlFormatException($"The block at offset {Position} fails its checksum.");

        Current = new NntlBlock(Position, kind, body);
        Position += NntlFormat.BlockHeaderSize + bodyLength;
        _afterIndex = kind == NntlBlockKind.Index;
        return true;
    }

    /// <summary>Releases the body buffer. The pipe reader is not completed.</summary>
    public void Dispose()
    {
        var body = _body;
        _body = [];
        if (body.Length > 0)
            ArrayPool<byte>.Shared.Return(body);
    }

    /// <summary>Fills <paramref name="destination"/> from the pipe, consuming as it copies.</summary>
    /// <returns>False when the stream ended before the first byte; throws when it ends part way.</returns>
    private async ValueTask<bool> FillAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            var result = await _input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var n = (int)Math.Min(buffer.Length, destination.Length - filled);
            buffer.Slice(0, n).CopyTo(destination.Span.Slice(filled));
            filled += n;
            _input.AdvanceTo(buffer.GetPosition(n));
            if (filled < destination.Length && (result.IsCompleted || result.IsCanceled))
            {
                _ended = true;
                if (filled == 0 && destination.Length == _header.Length && !_afterIndex)
                    return false;
                throw new NntlFormatException($"The stream ended inside the block at offset {Position}.");
            }
        }

        return true;
    }
}
