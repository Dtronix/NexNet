namespace NexNet.Inspector.Core.Format;

/// <summary>Random-access reader over a <c>.nntl</c> file.</summary>
/// <remarks>
/// <para>Opening uses the index footer when it is intact. Otherwise (a crashed recording, a torn write) it scans every
/// block from the header, resynchronizing on the sync marker after damage, and reports what it skipped in
/// <see cref="Damage"/>. Nothing but the index is loaded; blocks are read on demand by offset.</para>
/// <para>Not thread-safe. Bodies and the records decoded from them point into one reused buffer and are valid until
/// the next read; copy what must outlive it.</para>
/// </remarks>
public sealed class NntlReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly NntlBlockSource _source;
    private readonly List<NntlDamage> _damage = [];

    private NntlReader(Stream stream, NntlOpenMode mode, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _source = new NntlBlockSource(stream);
        try
        {
            Header = _source.ReadHeader();
            var footer = mode == NntlOpenMode.PreferFooter ? _source.TryReadFooter() : null;
            if (footer is not null)
            {
                Index = footer;
                IndexSource = NntlIndexSource.Footer;
            }
            else
            {
                Index = _source.Scan(_damage, out _);
                IndexSource = NntlIndexSource.Scan;
            }

            Sessions = LoadSessions();
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    /// <summary>Opens a recording.</summary>
    /// <param name="stream">Readable, seekable stream with the file starting at offset 0.</param>
    /// <param name="mode">Whether to trust the footer or always scan.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when the reader is disposed.</param>
    /// <exception cref="NntlFormatException">The stream does not start with a valid header block.</exception>
    public static NntlReader Open(Stream stream, NntlOpenMode mode = NntlOpenMode.PreferFooter, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new NntlReader(stream, mode, leaveOpen);
    }

    /// <summary>The file header.</summary>
    public NntlFileHeader Header { get; }

    /// <summary>Block index, from the footer or a scan.</summary>
    public NntlIndex Index { get; }

    /// <summary>Where <see cref="Index"/> came from. <see cref="NntlIndexSource.Scan"/> under
    /// <see cref="NntlOpenMode.PreferFooter"/> means the footer was missing or invalid (the UI shows a warning).</summary>
    public NntlIndexSource IndexSource { get; }

    /// <summary>Damaged regions found by a scan, in file order. Empty when the footer was used.</summary>
    public IReadOnlyList<NntlDamage> Damage => _damage;

    /// <summary>Length of the file.</summary>
    public long Length => _source.Length;

    /// <summary>Session-open records in file order. Their schema hashes are copies, safe to keep.</summary>
    public IReadOnlyList<NntlSessionOpen> Sessions { get; }

    /// <summary>Reads the block at <paramref name="offset"/>; false when it is missing or fails its checksum.</summary>
    public bool TryReadBlock(long offset, out NntlBlock block)
    {
        if (_source.TryReadBlock(offset, out var kind, out var body))
        {
            block = new NntlBlock(offset, kind, body);
            return true;
        }

        block = default;
        return false;
    }

    /// <summary>Reads the block at <paramref name="offset"/>.</summary>
    /// <exception cref="NntlFormatException">No valid block starts there.</exception>
    public NntlBlock ReadBlock(long offset) =>
        TryReadBlock(offset, out var block)
            ? block
            : throw new NntlFormatException($"No valid block at offset {offset}.");

    /// <summary>Reads and decodes the transport op block at <paramref name="offset"/>.</summary>
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

    /// <summary>Enumerates a session's transport ops whose ticks lie in [<paramref name="fromTicks"/>,
    /// <paramref name="toTicks"/>] (both inclusive), in file order. Blocks that fail to read are skipped.</summary>
    public TransportOpEnumerable ReadTransportOps(long sessionId, long fromTicks = long.MinValue, long toTicks = long.MaxValue) =>
        new(this, Index.FindGroup(NntlBlockKind.TransportOp, sessionId), fromTicks, toTicks);

    /// <summary>Finds the embedded schema with <paramref name="hash"/>. The returned bytes are copies.</summary>
    public bool TryGetSchema(ReadOnlySpan<byte> hash, out NntlSchema schema)
    {
        foreach (var entry in Index.GetEntries(NntlBlockKind.Schema))
        {
            if (!TryReadBlock(entry.Offset, out var block))
                continue;
            NntlSchema candidate;
            try
            {
                candidate = NntlCodec.DecodeSchema(block.Body);
            }
            catch (NntlFormatException)
            {
                continue;
            }

            if (candidate.Hash.Span.SequenceEqual(hash))
            {
                schema = new NntlSchema(candidate.Hash.ToArray(), candidate.Schema.ToArray());
                return true;
            }
        }

        schema = default;
        return false;
    }

    /// <summary>Releases the read buffer and the stream unless it was left open.</summary>
    public void Dispose()
    {
        _source.Dispose();
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private NntlSessionOpen[] LoadSessions()
    {
        List<NntlSessionOpen> sessions = [];
        foreach (var group in Index.Groups)
        {
            if (group.Kind != NntlBlockKind.SessionOpen)
                continue;
            foreach (var entry in group.Entries)
            {
                if (!TryReadBlock(entry.Offset, out var block))
                    continue;
                try
                {
                    var open = NntlCodec.DecodeSessionOpen(block.Body);
                    sessions.Add(open with { SchemaHash = open.SchemaHash.ToArray() });
                }
                catch (NntlFormatException)
                {
                }
            }
        }

        return sessions.ToArray();
    }

    /// <summary>Transport ops of one session in a tick range; see <see cref="ReadTransportOps"/>.</summary>
    public readonly struct TransportOpEnumerable
    {
        private readonly NntlReader _reader;
        private readonly NntlIndexGroup? _group;
        private readonly long _from;
        private readonly long _to;

        internal TransportOpEnumerable(NntlReader reader, NntlIndexGroup? group, long from, long to)
        {
            _reader = reader;
            _group = group;
            _from = from;
            _to = to;
        }

        /// <summary>Returns the enumerator.</summary>
        public TransportOpEnumerator GetEnumerator() => new(_reader, _group, _from, _to);
    }

    /// <summary>Enumerator over transport ops; <see cref="Current"/> is valid until the next <see cref="MoveNext"/>.</summary>
    public struct TransportOpEnumerator
    {
        private readonly NntlReader _reader;
        private readonly NntlIndexGroup? _group;
        private readonly long _from;
        private readonly long _to;
        private readonly int _end;
        private int _next;

        internal TransportOpEnumerator(NntlReader reader, NntlIndexGroup? group, long from, long to)
        {
            _reader = reader;
            _group = group;
            _from = from;
            _to = to;
            (_next, _end) = group?.GetCandidateRange(from, to) ?? (0, 0);
            Current = default;
        }

        /// <summary>The current op.</summary>
        public NntlTransportOp Current { get; private set; }

        /// <summary>Advances to the next op in range.</summary>
        public bool MoveNext()
        {
            while (_next < _end)
            {
                var entry = _group![_next++];
                if (entry.Ticks < _from || entry.Ticks > _to)
                    continue;
                if (_reader.TryReadTransportOp(entry.Offset, out var op))
                {
                    Current = op;
                    return true;
                }
            }

            return false;
        }
    }
}
