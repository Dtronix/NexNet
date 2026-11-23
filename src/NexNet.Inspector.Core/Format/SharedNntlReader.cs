namespace NexNet.Inspector.Core.Format;

/// <summary>
/// An <see cref="NntlReader"/> that several threads may use: block reads are serialized and every read hands back
/// copies, never the reader's reused buffer. A recording's background indexer and its viewers' on-demand byte reads
/// share one.
/// </summary>
/// <remarks>
/// The header, index, damage list and sessions are fixed once the reader is open, so they are read without the lock.
/// </remarks>
public sealed class SharedNntlReader : IDisposable
{
    private readonly NntlReader _reader;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Wraps an open reader; disposing this disposes it.</summary>
    /// <param name="reader">The reader. Nothing else may use it afterwards.</param>
    public SharedNntlReader(NntlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc cref="NntlReader.Header"/>
    public NntlFileHeader Header => _reader.Header;

    /// <inheritdoc cref="NntlReader.Index"/>
    public NntlIndex Index => _reader.Index;

    /// <inheritdoc cref="NntlReader.IndexSource"/>
    public NntlIndexSource IndexSource => _reader.IndexSource;

    /// <inheritdoc cref="NntlReader.Damage"/>
    public IReadOnlyList<NntlDamage> Damage => _reader.Damage;

    /// <inheritdoc cref="NntlReader.Sessions"/>
    public IReadOnlyList<NntlSessionOpen> Sessions => _reader.Sessions;

    /// <inheritdoc cref="NntlReader.Length"/>
    public long Length => _reader.Length;

    /// <summary>Reads a block; its body is a copy the caller owns.</summary>
    /// <param name="offset">Offset of the block.</param>
    /// <param name="block">The block.</param>
    /// <returns>False when no valid block starts there, or the reader was disposed.</returns>
    public bool TryReadBlock(long offset, out NntlBlock block)
    {
        lock (_gate)
        {
            if (!_disposed && _reader.TryReadBlock(offset, out var read))
            {
                block = read with { Body = read.Body.ToArray() };
                return true;
            }
        }

        block = default;
        return false;
    }

    /// <summary>Reads and decodes a transport op block; its bytes are a copy the caller owns.</summary>
    /// <param name="offset">Offset of the block.</param>
    /// <param name="op">The op.</param>
    /// <returns>False when the block is missing, not a transport op, or malformed.</returns>
    public bool TryReadTransportOp(long offset, out NntlTransportOp op)
    {
        if (TryReadBlock(offset, out var block) && block.Kind == NntlBlockKind.TransportOp)
        {
            try
            {
                op = NntlCodec.DecodeTransportOp(block.Body);
                return true;
            }
            catch (NntlFormatException)
            {
            }
        }

        op = default;
        return false;
    }

    /// <inheritdoc cref="NntlReader.TryGetSchema"/>
    public bool TryGetSchema(ReadOnlySpan<byte> hash, out NntlSchema schema)
    {
        lock (_gate)
        {
            if (!_disposed)
                return _reader.TryGetSchema(hash, out schema);
        }

        schema = default;
        return false;
    }

    /// <summary>Disposes the reader; later reads fail.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _reader.Dispose();
        }
    }
}
