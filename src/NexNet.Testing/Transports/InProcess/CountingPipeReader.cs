using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Quiescence;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// <see cref="PipeReader"/> wrapper that decrements <see cref="QuiescenceCounters.BytesInTransit"/>
/// each time the consumer advances past committed bytes, matching the increment performed by the
/// paired <see cref="CountingPipeWriter"/>. The delta is computed from the current
/// <see cref="ReadResult.Buffer"/> start to the supplied <c>consumed</c> position.
/// </summary>
internal sealed class CountingPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly QuiescenceCounters _counters;
    private readonly QuiescenceTracker _tracker;
    private ReadOnlySequence<byte> _lastBuffer;
    private bool _hasLastBuffer;

    public CountingPipeReader(PipeReader inner, QuiescenceCounters counters, QuiescenceTracker tracker)
    {
        _inner = inner;
        _counters = counters;
        _tracker = tracker;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        RecordConsumed(consumed);
        _inner.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        RecordConsumed(consumed);
        _inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _inner.Complete(exception);

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var task = _inner.ReadAsync(cancellationToken);
        return task.IsCompletedSuccessfully ? new ValueTask<ReadResult>(StoreBuffer(task.Result)) : Wait(task);
    }

    private async ValueTask<ReadResult> Wait(ValueTask<ReadResult> task)
    {
        var result = await task.ConfigureAwait(false);
        return StoreBuffer(result);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (_inner.TryRead(out result))
        {
            result = StoreBuffer(result);
            return true;
        }
        return false;
    }

    private ReadResult StoreBuffer(ReadResult result)
    {
        _lastBuffer = result.Buffer;
        _hasLastBuffer = true;
        return result;
    }

    private void RecordConsumed(SequencePosition consumed)
    {
        if (!_hasLastBuffer)
            return;

        var consumedLength = _lastBuffer.Slice(0, consumed).Length;
        if (consumedLength > 0)
        {
            _counters.AddBytesInTransit(-(int)consumedLength);
            _tracker.SignalChange();
        }
        _hasLastBuffer = false;
    }
}
