using NexNet.Inspector.Core.Format;

namespace NexNet.Inspector.Core.Assembly;

/// <summary>One transport op of a <see cref="TransportOpStore"/>.</summary>
/// <param name="Offset">Absolute stream offset of the op's first byte.</param>
/// <param name="Length">Bytes the op carried.</param>
/// <param name="OpId">The tap's id of the op.</param>
/// <param name="Ticks">Stopwatch ticks of the op.</param>
/// <param name="Source">Where the bytes live: the block offset in a recording; unused for a live store.</param>
public readonly record struct StreamOp(long Offset, int Length, long OpId, long Ticks, long Source)
{
    /// <summary>Offset one past the op's last byte.</summary>
    public long End => Offset + Length;
}

/// <summary>A range of stream bytes, in absolute stream offsets.</summary>
/// <param name="Offset">First byte.</param>
/// <param name="Length">Number of bytes.</param>
public readonly record struct StreamRange(long Offset, long Length)
{
    /// <summary>Offset one past the last byte.</summary>
    public long End => Offset + Length;
}

/// <summary>
/// The bytes of one direction of a capture, kept as the transport ops that carried them: an <see cref="IStreamBytes"/>
/// that also answers where each op begins (the hex view's read boundaries), which bytes were redacted and where a gap
/// lost bytes. Offsets match <see cref="Framing.FrameParser"/>'s: a gap advances them by the bytes it lost.
/// </summary>
/// <remarks>
/// Not thread-safe: a capture session guards its stores and its assembler with one lock. Subclasses decide where op
/// bytes are kept (in memory for a live capture, in the recording for a file).
/// </remarks>
public abstract class TransportOpStore : IStreamBytes
{
    private readonly List<StreamRange> _redactions = [];
    private readonly List<StreamRange> _gaps = [];
    private StreamOp[] _ops = new StreamOp[64];
    private int _head;
    private int _count;
    private long _start;
    private long _end;

    /// <summary>Creates an empty store.</summary>
    /// <param name="startOffset">Absolute offset of the first byte that will be appended.</param>
    protected TransportOpStore(long startOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        _start = startOffset;
        _end = startOffset;
    }

    /// <inheritdoc/>
    public long StartOffset => _start;

    /// <inheritdoc/>
    public long EndOffset => _end;

    /// <summary>The ops still held, in stream order.</summary>
    public ReadOnlySpan<StreamOp> Ops => _ops.AsSpan(_head, _count - _head);

    /// <summary>Redacted byte ranges, in stream order.</summary>
    public ReadOnlySpan<StreamRange> Redactions => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_redactions);

    /// <summary>Ranges whose bytes a gap lost, in stream order.</summary>
    public ReadOnlySpan<StreamRange> Gaps => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_gaps);

    /// <summary>Bytes appended, gaps excluded.</summary>
    public long ByteCount { get; private set; }

    /// <summary>Appends one op.</summary>
    /// <param name="op">The op; its bytes are copied or kept as the subclass requires, never borrowed past the call.</param>
    /// <param name="source">Where the op came from: its block offset in a recording; ignored by a live store.</param>
    public void Append(in NntlTransportOp op, long source)
    {
        var length = op.Bytes.Length;
        if (length == 0)
            return;

        if (_count == _ops.Length)
            Grow();
        var offset = _end;
        var index = _count++;
        _ops[index] = new StreamOp(offset, length, op.OpId, op.Ticks, source);
        foreach (var range in op.Redactions.Span)
        {
            if (range.Length > 0)
                _redactions.Add(new StreamRange(offset + range.Start, range.Length));
        }

        _end += length;
        ByteCount += length;
        OnAppended(index, op.Bytes);
    }

    /// <summary>Records bytes a gap lost: the next op starts that much further on.</summary>
    /// <param name="droppedBytes">Bytes the lost ops carried.</param>
    public void SkipGap(long droppedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(droppedBytes);
        if (droppedBytes == 0)
            return;
        _gaps.Add(new StreamRange(_end, droppedBytes));
        _end += droppedBytes;
    }

    /// <summary>Finds the op that holds a byte.</summary>
    /// <param name="offset">Absolute stream offset.</param>
    /// <param name="index">Index into <see cref="Ops"/>.</param>
    /// <returns>False when the byte is trimmed, not yet appended or inside a gap.</returns>
    public bool TryFindOp(long offset, out int index)
    {
        index = -1;
        if (offset < _start || offset >= _end || _count == _head)
            return false;

        var lo = _head;
        var hi = _count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) >> 1);
            if (_ops[mid].Offset <= offset)
                lo = mid;
            else
                hi = mid - 1;
        }

        var op = _ops[lo];
        if (offset < op.Offset || offset >= op.End)
            return false;
        index = lo - _head;
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetSegment(long offset, out ReadOnlyMemory<byte> segment)
    {
        segment = default;
        if (!TryFindOp(offset, out var index))
            return false;

        var absolute = index + _head;
        if (!TryGetOpBytes(absolute, _ops[absolute], out var bytes))
            return false;
        segment = bytes.Slice((int)(offset - _ops[absolute].Offset));
        return true;
    }

    /// <summary>Called after an op is appended, with its bytes, which are only valid during the call.</summary>
    /// <param name="index">The op's absolute index (stable across trims).</param>
    /// <param name="bytes">The op's bytes.</param>
    protected abstract void OnAppended(int index, ReadOnlyMemory<byte> bytes);

    /// <summary>Gets the bytes of an op.</summary>
    /// <param name="index">The op's absolute index.</param>
    /// <param name="op">The op.</param>
    /// <param name="bytes">Its bytes; they stay valid after the call (never a reused buffer).</param>
    /// <returns>False when they cannot be had (the recording no longer reads).</returns>
    protected abstract bool TryGetOpBytes(int index, in StreamOp op, out ReadOnlyMemory<byte> bytes);

    /// <summary>Called for each op a trim drops, oldest first.</summary>
    /// <param name="index">The op's absolute index.</param>
    protected virtual void OnTrimmed(int index)
    {
    }

    /// <summary>Drops the oldest ops until at most <paramref name="keepBytes"/> appended bytes remain.</summary>
    /// <param name="keepBytes">Bytes to keep.</param>
    /// <returns>Bytes dropped.</returns>
    protected long TrimOldest(long keepBytes)
    {
        long dropped = 0;
        while (_head < _count && ByteCount - dropped > keepBytes)
        {
            dropped += _ops[_head].Length;
            OnTrimmed(_head);
            _head++;
        }

        ByteCount -= dropped;
        _start = _head < _count ? _ops[_head].Offset : _end;
        return dropped;
    }

    /// <summary>The absolute index of the oldest op still held.</summary>
    protected int Head => _head;

    // Trimmed entries stay in the array so absolute indices never move; an entry is 40 bytes, far less than the op
    // bytes a trim frees.
    private void Grow() => Array.Resize(ref _ops, _ops.Length * 2);
}
