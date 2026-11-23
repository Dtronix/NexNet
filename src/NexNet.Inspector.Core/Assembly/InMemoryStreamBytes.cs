namespace NexNet.Inspector.Core.Assembly;

/// <summary>
/// An <see cref="IStreamBytes"/> over retained op buffers: each appended op is kept as given (no copy) and found by
/// binary search. Suits tests and a live capture that keeps its ops in memory.
/// </summary>
/// <remarks>Not thread-safe.</remarks>
public sealed class InMemoryStreamBytes : IStreamBytes
{
    private readonly List<Entry> _entries = [];
    private int _head;
    private long _start;
    private long _end;

    /// <summary>Creates an empty stream.</summary>
    /// <param name="startOffset">Absolute offset of the first byte that will be appended.</param>
    public InMemoryStreamBytes(long startOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        _start = startOffset;
        _end = startOffset;
    }

    /// <inheritdoc/>
    public long StartOffset => _start;

    /// <inheritdoc/>
    public long EndOffset => _end;

    /// <summary>Appends one op. The buffer is retained, not copied: the caller must not reuse it.</summary>
    /// <param name="op">The op's bytes.</param>
    public void Append(ReadOnlyMemory<byte> op)
    {
        if (op.IsEmpty)
            return;

        _entries.Add(new Entry(_end, op));
        _end += op.Length;
    }

    /// <summary>Appends a copy of one op, for callers whose buffer is borrowed.</summary>
    /// <param name="op">The op's bytes.</param>
    public void AppendCopy(ReadOnlySpan<byte> op)
    {
        if (!op.IsEmpty)
            Append(op.ToArray());
    }

    /// <summary>Drops the ops that end at or before <paramref name="offset"/>.</summary>
    /// <param name="offset">Absolute offset; bytes of an op that straddles it are kept.</param>
    public void TrimBefore(long offset)
    {
        while (_head < _entries.Count && _entries[_head].Offset + _entries[_head].Bytes.Length <= offset)
            _entries[_head++] = default;

        _start = _head < _entries.Count ? _entries[_head].Offset : Math.Max(_start, Math.Min(offset, _end));

        // Compact once the dropped prefix dominates, so trimming stays amortised O(1) per op.
        if (_head > 64 && _head * 2 > _entries.Count)
        {
            _entries.RemoveRange(0, _head);
            _head = 0;
        }
    }

    /// <inheritdoc/>
    public bool TryGetSegment(long offset, out ReadOnlyMemory<byte> segment)
    {
        segment = default;
        if (offset < _start || offset >= _end)
            return false;

        var lo = _head;
        var hi = _entries.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) >> 1);
            if (_entries[mid].Offset <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }

        var entry = _entries[lo];
        var at = offset - entry.Offset;
        if (at < 0 || at >= entry.Bytes.Length)
            return false;

        segment = entry.Bytes.Slice((int)at);
        return true;
    }

    private readonly record struct Entry(long Offset, ReadOnlyMemory<byte> Bytes);
}
