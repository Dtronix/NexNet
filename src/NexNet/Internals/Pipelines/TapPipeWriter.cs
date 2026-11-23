using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Diagnostics;
using NexNet.Logging;

namespace NexNet.Internals.Pipelines;

/// <summary>
/// Session output that reports to an <see cref="INexusSessionTap"/> the bytes committed since the last flush, once, just
/// before the flush. Only installed when a tap is configured; otherwise the session writes the transport's writer
/// directly.
/// </summary>
/// <remarks>
/// The bytes are not copied: the wrapper remembers the regions of the inner writer's buffers that were committed and
/// hands them to the tap before the flush. That relies on the inner writer keeping committed bytes in place until
/// it is flushed, which the transports' writers (<see cref="Pipe"/>, <see cref="StreamSendPipe"/> and the stream pipe
/// writer) all do.
/// </remarks>
internal sealed class TapPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private readonly INexusLogger? _logger;

    // Cleared when the tap fails: the session keeps running untapped.
    private INexusSessionTap? _tap;

    // The buffer last handed out, and the committed regions not yet flushed. Consecutive commits into one buffer
    // extend the same region.
    private Memory<byte> _buffer;
    private Region[] _regions = new Region[4];
    private int _regionCount;

    // Reused to present several regions as one sequence without allocating per flush.
    private Segment[] _segments = [];

    public TapPipeWriter(PipeWriter inner, INexusSessionTap tap, INexusLogger? logger)
    {
        _inner = inner;
        _tap = tap;
        _logger = logger;
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
        => _buffer = _inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0)
        => (_buffer = _inner.GetMemory(sizeHint)).Span;

    public override void Advance(int bytes)
    {
        _inner.Advance(bytes);
        if (_tap == null || bytes <= 0)
            return;

        Commit(_buffer, bytes);

        // Writers allow several Advance calls on one buffer; the next one continues after these bytes.
        _buffer = _buffer.Slice(bytes);
    }

    public override bool CanGetUnflushedBytes => _inner.CanGetUnflushedBytes;

    public override long UnflushedBytes => _inner.UnflushedBytes;

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_regionCount == 0)
            return _inner.FlushAsync(cancellationToken);

        var tapped = Report();
        return tapped.IsCompletedSuccessfully
            ? _inner.FlushAsync(cancellationToken)
            : FlushAfterTap(tapped, cancellationToken);
    }

    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
    {
        // Completing commits what is still buffered, so it reaches the peer: report it. Complete cannot wait for
        // a tap that holds it.
        if (_regionCount > 0)
        {
            var tapped = Report();
            if (!tapped.IsCompletedSuccessfully)
                _ = Observe(tapped);
        }

        _buffer = default;
        _inner.Complete(exception);
    }

    private void Commit(Memory<byte> buffer, int bytes)
    {
        if (_regionCount > 0)
        {
            ref var last = ref _regions[_regionCount - 1];
            if (last.Length + bytes <= last.Buffer.Length
                && Unsafe.AreSame(
                    ref Unsafe.Add(ref MemoryMarshal.GetReference(last.Buffer.Span), last.Length),
                    ref MemoryMarshal.GetReference(buffer.Span)))
            {
                last.Length += bytes;
                return;
            }
        }

        if (_regionCount == _regions.Length)
            Array.Resize(ref _regions, _regions.Length * 2);
        _regions[_regionCount++] = new Region(buffer, bytes);
    }

    private ValueTask Report()
    {
        var data = BuildSequence();
        _regions.AsSpan(0, _regionCount).Clear();
        _regionCount = 0;

        if (_tap == null)
            return default;

        try
        {
            return _tap.OnWrite(data, Stopwatch.GetTimestamp());
        }
        catch (Exception e)
        {
            Fail(e);
            return default;
        }
    }

    private ReadOnlySequence<byte> BuildSequence()
    {
        if (_regionCount == 1)
            return new ReadOnlySequence<byte>(_regions[0].Committed);

        if (_segments.Length < _regionCount)
        {
            var grown = new Segment[Math.Max(_regionCount, _segments.Length * 2)];
            _segments.CopyTo(grown, 0);
            for (var i = _segments.Length; i < grown.Length; i++)
                grown[i] = new Segment();
            _segments = grown;
        }

        long running = 0;
        for (var i = 0; i < _regionCount; i++)
        {
            var memory = _regions[i].Committed;
            _segments[i].Set(memory, running, i + 1 < _regionCount ? _segments[i + 1] : null);
            running += memory.Length;
        }

        var lastSegment = _segments[_regionCount - 1];
        return new ReadOnlySequence<byte>(_segments[0], 0, lastSegment, lastSegment.Memory.Length);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<FlushResult> FlushAfterTap(ValueTask tapped, CancellationToken cancellationToken)
    {
        await Observe(tapped).ConfigureAwait(false);
        return await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask Observe(ValueTask tapped)
    {
        try
        {
            await tapped.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void Fail(Exception e)
    {
        _tap = null;
        _regions.AsSpan(0, _regionCount).Clear();
        _regionCount = 0;
        _logger?.LogError(e, "Transport tap failed on a write; this session's writes are no longer tapped.");
    }

    private struct Region(Memory<byte> buffer, int length)
    {
        public readonly Memory<byte> Buffer = buffer;
        public int Length = length;
        public readonly ReadOnlyMemory<byte> Committed => Buffer.Slice(0, Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public void Set(ReadOnlyMemory<byte> memory, long runningIndex, Segment? next)
        {
            Memory = memory;
            RunningIndex = runningIndex;
            Next = next;
        }
    }
}
