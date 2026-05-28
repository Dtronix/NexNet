using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Quiescence;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// <see cref="PipeWriter"/> wrapper that increments a <see cref="QuiescenceCounters"/>
/// bytes-in-transit count every time the producer commits bytes. The matching
/// <see cref="CountingPipeReader"/> decrements as those bytes are consumed.
/// </summary>
internal sealed class CountingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private readonly QuiescenceCounters _counters;
    private readonly QuiescenceTracker _tracker;

    public CountingPipeWriter(PipeWriter inner, QuiescenceCounters counters, QuiescenceTracker tracker)
    {
        _inner = inner;
        _counters = counters;
        _tracker = tracker;
    }

    public override void Advance(int bytes)
    {
        _inner.Advance(bytes);
        if (bytes > 0)
        {
            _counters.AddBytesInTransit(bytes);
            _tracker.SignalChange();
        }
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        => _inner.FlushAsync(cancellationToken);
}
