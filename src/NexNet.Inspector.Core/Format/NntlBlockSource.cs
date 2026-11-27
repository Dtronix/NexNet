using System.Buffers;
using System.Buffers.Binary;

namespace NexNet.Inspector.Core.Format;

/// <summary>Reads and verifies blocks from a seekable stream; shared by <see cref="NntlReader"/> and
/// <see cref="NntlFile.RebuildIndex"/>. Not thread-safe: one body buffer is reused for every read.</summary>
internal sealed class NntlBlockSource : IDisposable
{
    private const int ScanWindow = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[NntlFormat.BlockHeaderSize];
    private byte[] _body = ArrayPool<byte>.Shared.Rent(4096);

    public NntlBlockSource(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Reading a .nntl file needs a readable, seekable stream.", nameof(stream));
        _stream = stream;
        Length = stream.Length;
    }

    public long Length { get; }

    /// <summary>Reads the block at <paramref name="offset"/> when its header is sane and its checksum matches.
    /// <paramref name="body"/> is valid until the next read.</summary>
    public bool TryReadBlock(long offset, out NntlBlockKind kind, out ReadOnlyMemory<byte> body)
    {
        kind = default;
        body = default;
        if (offset < 0 || Length - offset < NntlFormat.BlockHeaderSize)
            return false;

        _stream.Position = offset;
        _stream.ReadExactly(_header);
        var length = NntlFormat.ParseBlockHeader(_header, Length - offset - NntlFormat.BlockHeaderSize);
        if (length < 0)
            return false;

        if (_body.Length < length)
        {
            ArrayPool<byte>.Shared.Return(_body);
            _body = ArrayPool<byte>.Shared.Rent(length);
        }

        var span = _body.AsSpan(0, length);
        _stream.ReadExactly(span);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(12));
        if (stored != NntlFormat.ComputeChecksum(_header.AsSpan(4, 8), span))
            return false;

        kind = (NntlBlockKind)_header[4];
        body = _body.AsMemory(0, length);
        return true;
    }

    /// <summary>Returns the offset of the first valid block at or after <paramref name="from"/>, or -1.</summary>
    public long FindNextBlock(long from)
    {
        var window = ArrayPool<byte>.Shared.Rent(ScanWindow);
        try
        {
            var sync = NntlFormat.SyncMarker;
            var pos = from;
            while (Length - pos >= NntlFormat.BlockHeaderSize)
            {
                var count = (int)Math.Min(ScanWindow, Length - pos);
                _stream.Position = pos;
                _stream.ReadExactly(window, 0, count);
                var i = 0;
                while (true)
                {
                    var hit = window.AsSpan(i, count - i).IndexOf(sync);
                    if (hit < 0)
                        break;
                    var candidate = pos + i + hit;
                    if (TryReadBlock(candidate, out _, out _))
                        return candidate;
                    i += hit + 1;
                }

                if (pos + count >= Length)
                    break;
                // Overlap windows so a marker straddling the boundary is still seen.
                pos += count - (sync.Length - 1);
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    /// <summary>Returns the index offset named by a trailer that ends the stream, or -1.</summary>
    public long ReadTrailer()
    {
        if (Length < NntlFormat.TrailerSize)
            return -1;
        Span<byte> trailer = stackalloc byte[NntlFormat.TrailerSize];
        _stream.Position = Length - NntlFormat.TrailerSize;
        _stream.ReadExactly(trailer);
        return NntlFormat.ParseTrailer(trailer);
    }

    /// <summary>Reads and decodes the header block, which must open the file.</summary>
    public NntlFileHeader ReadHeader()
    {
        if (!TryReadBlock(0, out var kind, out var body) || kind != NntlBlockKind.Header)
            throw new NntlFormatException("The stream does not start with a valid .nntl header block.");

        var header = NntlCodec.DecodeHeader(body);
        if (header.FormatVersion > NntlFormat.Version || header.FormatVersion < 1)
            throw new NntlFormatException($"Format version {header.FormatVersion} is not supported (this build reads up to {NntlFormat.Version}).");
        return header;
    }

    /// <summary>Loads the footer index when the trailer, the index block and their placement all check out.</summary>
    public NntlIndex? TryReadFooter()
    {
        var offset = ReadTrailer();
        if (offset < NntlFormat.BlockHeaderSize || offset >= Length - NntlFormat.TrailerSize)
            return null;
        if (!TryReadBlock(offset, out var kind, out var body) || kind != NntlBlockKind.Index)
            return null;
        if (offset + NntlFormat.BlockHeaderSize + body.Length != Length - NntlFormat.TrailerSize)
            return null;

        try
        {
            return NntlCodec.DecodeIndex(body);
        }
        catch (NntlFormatException)
        {
            return null;
        }
    }

    /// <summary>Walks every block from the start, resynchronizing on the sync marker after damage.</summary>
    /// <param name="damage">Receives each damaged region in file order.</param>
    /// <param name="contentEnd">End of the last valid non-index block: where a rebuilt footer goes.</param>
    public NntlIndex Scan(List<NntlDamage> damage, out long contentEnd)
    {
        var index = new NntlIndex();
        contentEnd = 0;
        long pos = 0;
        while (pos < Length)
        {
            if (TryReadBlock(pos, out var kind, out var body))
            {
                var end = pos + NntlFormat.BlockHeaderSize + body.Length;
                if (kind == NntlBlockKind.Index)
                {
                    // A closed file ends with its index and a trailer pointing back at it; those 16 bytes are not damage.
                    if (Length - end == NntlFormat.TrailerSize && ReadTrailer() == pos)
                        break;
                    pos = end;
                    continue;
                }

                try
                {
                    NntlCodec.GetIndexKey(kind, body, out var sessionId, out var ticks);
                    index.Add(kind, sessionId, pos, ticks);
                }
                catch (NntlFormatException)
                {
                    damage.Add(new NntlDamage(NntlDamageKind.Malformed, pos, end - pos));
                }

                contentEnd = end;
                pos = end;
                continue;
            }

            var next = FindNextBlock(pos + 1);
            if (next < 0)
            {
                damage.Add(new NntlDamage(NntlDamageKind.TornTail, pos, Length - pos));
                break;
            }

            damage.Add(new NntlDamage(NntlDamageKind.Corrupt, pos, next - pos));
            pos = next;
        }

        return index;
    }

    public void Dispose()
    {
        var body = _body;
        _body = [];
        if (body.Length > 0)
            ArrayPool<byte>.Shared.Return(body);
    }
}
