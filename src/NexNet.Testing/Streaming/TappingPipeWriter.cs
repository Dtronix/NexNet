using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Testing.Streaming;

/// <summary>
/// <see cref="PipeWriter"/> wrapper that records the bytes the handler advances past in the
/// writer's buffer. We tap at <see cref="Advance"/> rather than at <see cref="FlushAsync"/>
/// because the visible memory is overwritten as the writer recycles segments; the
/// just-advanced range is the only window we are guaranteed to read correctly.
/// </summary>
internal sealed class TappingPipeWriter : PipeWriter
{
    private readonly PipeWriter _inner;
    private readonly PipeRecording _recording;
    private Memory<byte> _lastMemory;

    public TappingPipeWriter(PipeWriter inner, PipeRecording recording)
    {
        _inner = inner;
        _recording = recording;
    }

    public override void Advance(int bytes)
    {
        if (bytes > 0 && bytes <= _lastMemory.Length)
            _recording.RecordWritten(_lastMemory.Span.Slice(0, bytes));
        _lastMemory = Memory<byte>.Empty;
        _inner.Advance(bytes);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        _lastMemory = _inner.GetMemory(sizeHint);
        return _lastMemory;
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        // The tap can only record the contiguous Memory; falling back to Span loses the
        // ability to record, so we re-request as Memory. PipeWriter implementations are free
        // to satisfy GetSpan from the same backing buffer.
        _lastMemory = _inner.GetMemory(sizeHint);
        return _lastMemory.Span;
    }

    public override void CancelPendingFlush() => _inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null)
    {
        _recording.RecordCompletion(exception);
        _inner.Complete(exception);
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        => _inner.FlushAsync(cancellationToken);
}
