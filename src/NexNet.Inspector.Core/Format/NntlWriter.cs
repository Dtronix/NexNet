using NexNet.Inspector.Core.Model;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Format;

/// <summary>Appends blocks to a <c>.nntl</c> stream and writes the index footer on close.</summary>
/// <remarks>
/// <para>The output only needs to be writable: offsets are counted by the writer, so a non-seekable stream (a pipe,
/// a network stream) works. Offsets are relative to the first byte this writer wrote.</para>
/// <para>Thread safety: every public method may be called concurrently. One lock serializes appends so blocks never
/// interleave; a block is encoded into a reused buffer and written to the stream in one call while the lock is held.
/// The tap's per-viewer ring keeps this off NexNet's per-frame path, so the lock is not contended by the transport.</para>
/// <para>Durability: blocks are handed to the stream as they are appended; call <see cref="Flush"/> to push them
/// further (for example on a timer). A crash leaves every block the stream persisted readable; only the footer and
/// a torn last block are lost, and <see cref="NntlReader"/> scans past both.</para>
/// <para>If the stream throws, the writer is faulted and later calls throw, because the offset of a partly written
/// block is unknown.</para>
/// </remarks>
public sealed class NntlWriter : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly PooledBufferWriter _buffer = new();
    private readonly NntlIndex _index = new();
    private readonly List<byte[]> _schemaHashes = [];
    private long _position;
    private bool _closed;
    private bool _faulted;

    /// <summary>Creates a writer and writes the header block.</summary>
    /// <param name="output">Writable destination.</param>
    /// <param name="header">File header; usually <see cref="NntlFileHeader.Create"/>.</param>
    /// <param name="leaveOpen">True to leave <paramref name="output"/> open when the writer is disposed.</param>
    public NntlWriter(Stream output, in NntlFileHeader header, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        if (header.FormatVersion != NntlFormat.Version)
            throw new ArgumentException($"This writer produces format version {NntlFormat.Version}.", nameof(header));

        _output = output;
        _leaveOpen = leaveOpen;
        Header = header;

        lock (_gate)
        {
            var w = Begin();
            NntlCodec.WriteHeader(ref w, header);
            End(ref w, NntlBlockKind.Header, 0, header.StopwatchTicksAtAnchor);
        }
    }

    /// <summary>The header written at the start of the file.</summary>
    public NntlFileHeader Header { get; }

    /// <summary>Bytes written so far.</summary>
    public long Position
    {
        get
        {
            lock (_gate)
                return _position;
        }
    }

    /// <summary>Embeds a schema unless one with the same hash was already written.</summary>
    /// <returns>True when the schema block was written.</returns>
    public bool WriteSchema(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> schema)
    {
        if (hash.IsEmpty)
            throw new ArgumentException("A schema needs a hash.", nameof(hash));

        lock (_gate)
        {
            ThrowIfUnusable();
            foreach (var known in _schemaHashes)
            {
                if (hash.SequenceEqual(known))
                    return false;
            }

            var w = Begin();
            NntlCodec.WriteSchema(ref w, hash, schema);
            End(ref w, NntlBlockKind.Schema, 0, 0);
            _schemaHashes.Add(hash.ToArray());
            return true;
        }
    }

    /// <summary>Writes a session-open block.</summary>
    public void WriteSessionOpen(in NntlSessionOpen session)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            var w = Begin();
            NntlCodec.WriteSessionOpen(ref w, session);
            End(ref w, NntlBlockKind.SessionOpen, session.SessionId, session.Ticks);
        }
    }

    /// <summary>Writes a transport op block.</summary>
    /// <param name="sessionId">Owning session.</param>
    /// <param name="direction">Read or write.</param>
    /// <param name="opId">Op sequence number.</param>
    /// <param name="ticks">Stopwatch ticks of the op.</param>
    /// <param name="bytes">Bytes the op moved (already redacted where <paramref name="redactions"/> says so).</param>
    /// <param name="redactions">Ranges of <paramref name="bytes"/> that were replaced; sets <see cref="NntlTransportOpFlags.Redacted"/>.</param>
    public void WriteTransportOp(
        long sessionId,
        TapDirection direction,
        long opId,
        long ticks,
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<NntlByteRange> redactions = default)
    {
        foreach (var r in redactions)
        {
            if (r.Start < 0 || r.Length < 0 || r.Start > bytes.Length - r.Length)
                throw new ArgumentOutOfRangeException(nameof(redactions), "A redaction range lies outside the op's bytes.");
        }

        var flags = redactions.IsEmpty ? NntlTransportOpFlags.None : NntlTransportOpFlags.Redacted;
        lock (_gate)
        {
            ThrowIfUnusable();
            var w = Begin();
            NntlCodec.WriteTransportOp(ref w, sessionId, direction, opId, ticks, flags, redactions, bytes);
            End(ref w, NntlBlockKind.TransportOp, sessionId, ticks);
        }
    }

    /// <summary>Writes a transport op block from a decoded record.</summary>
    public void WriteTransportOp(in NntlTransportOp op) =>
        WriteTransportOp(op.SessionId, op.Direction, op.OpId, op.Ticks, op.Bytes.Span, op.Redactions.Span);

    /// <summary>Writes a gap block.</summary>
    public void WriteGap(in NntlGap gap)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            var w = Begin();
            NntlCodec.WriteGap(ref w, gap);
            End(ref w, NntlBlockKind.Gap, gap.SessionId, gap.FromTicks);
        }
    }

    /// <summary>Writes a snapshot block at <see cref="NntlSnapshot.CurrentVersion"/>.</summary>
    public void WriteSnapshot(long sessionId, long ticks, ReadOnlySpan<NntlSnapshotSection> sections)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            var w = Begin();
            NntlCodec.WriteSnapshot(ref w, sessionId, ticks, sections);
            End(ref w, NntlBlockKind.Snapshot, sessionId, ticks);
        }
    }

    /// <summary>Writes a session-close block.</summary>
    public void WriteSessionClose(in NntlSessionClose close)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            var w = Begin();
            NntlCodec.WriteSessionClose(ref w, close);
            End(ref w, NntlBlockKind.SessionClose, close.SessionId, close.Ticks);
        }
    }

    /// <summary>Flushes the output stream.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            _output.Flush();
        }
    }

    /// <summary>Writes the index footer and trailer and flushes. Later appends throw.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            ThrowIfUnusable();
            _closed = true;
            try
            {
                WriteFooter(_buffer, _index, _position, _output);
                _output.Flush();
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    /// <summary>Closes the file (unless faulted) and releases the stream unless it was left open.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                if (!_closed && !_faulted)
                    Close();
            }
            finally
            {
                _closed = true;
                _buffer.Dispose();
                if (!_leaveOpen)
                    _output.Dispose();
            }
        }
    }

    /// <summary>Encodes the index block and trailer for an index starting at <paramref name="indexOffset"/>.</summary>
    internal static void WriteFooter(PooledBufferWriter buffer, NntlIndex index, long indexOffset, Stream output)
    {
        buffer.Reset();
        buffer.Advance(NntlFormat.BlockHeaderSize);
        var w = new MsgPackWriter(buffer);
        NntlCodec.WriteIndex(ref w, index);
        w.Flush();
        CheckBodyLength(buffer.WrittenCount - NntlFormat.BlockHeaderSize);
        NntlFormat.WriteBlockHeader(buffer.WrittenSpan, NntlBlockKind.Index);
        NntlFormat.WriteTrailer(buffer.GetSpan(NntlFormat.TrailerSize), indexOffset);
        buffer.Advance(NntlFormat.TrailerSize);
        output.Write(buffer.WrittenSpan);
    }

    private MsgPackWriter Begin()
    {
        _buffer.Reset();
        _buffer.Advance(NntlFormat.BlockHeaderSize);
        return new MsgPackWriter(_buffer);
    }

    private void End(ref MsgPackWriter w, NntlBlockKind kind, long sessionId, long ticks)
    {
        w.Flush();
        CheckBodyLength(_buffer.WrittenCount - NntlFormat.BlockHeaderSize);
        var block = _buffer.WrittenSpan;
        NntlFormat.WriteBlockHeader(block, kind);
        try
        {
            _output.Write(block);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        _index.Add(kind, sessionId, _position, ticks);
        _position += block.Length;
    }

    private static void CheckBodyLength(int length)
    {
        if (length > NntlFormat.MaxBodyLength)
            throw new ArgumentException($"A block body of {length} bytes exceeds the {NntlFormat.MaxBodyLength} byte limit.");
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_faulted)
            throw new InvalidOperationException("The writer is faulted: an earlier write to the output stream failed.");
    }
}
