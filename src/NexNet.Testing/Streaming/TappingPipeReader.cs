using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Streaming;

/// <summary>
/// <see cref="PipeReader"/> wrapper that records the slice of bytes the handler commits past
/// each time it calls AdvanceTo. "What the handler saw" is the consumed slice rather than the
/// visible buffer because peek-without-consume is a normal pattern.
/// </summary>
internal sealed class TappingPipeReader : PipeReader
{
    private readonly PipeReader _inner;
    private readonly PipeRecording _recording;
    private ReadOnlySequence<byte> _lastBuffer;
    private bool _hasLastBuffer;

    public TappingPipeReader(PipeReader inner, PipeRecording recording)
    {
        _inner = inner;
        _recording = recording;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        RecordConsumedSlice(consumed);
        _inner.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        RecordConsumedSlice(consumed);
        _inner.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        _recording.RecordCompletion(exception);
        _inner.Complete(exception);
    }

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
        if (result.IsCompleted)
            _recording.RecordCompletion(null);
        return result;
    }

    private void RecordConsumedSlice(SequencePosition consumed)
    {
        if (!_hasLastBuffer)
            return;

        var slice = _lastBuffer.Slice(0, consumed);
        if (slice.Length == 0)
            return;

        if (slice.IsSingleSegment)
        {
            _recording.RecordConsumed(slice.FirstSpan);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent((int)slice.Length);
            try
            {
                slice.CopyTo(rented);
                _recording.RecordConsumed(rented.AsSpan(0, (int)slice.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        _hasLastBuffer = false;
    }
}
