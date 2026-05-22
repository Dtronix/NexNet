using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Pipes;

namespace NexNet.Testing.Streaming;

/// <summary>
/// <see cref="INexusDuplexPipe"/> wrapper that exposes a tapping reader/writer pair to the
/// handler while delegating every other interface member to the inner pipe. The pipe manager's
/// internal active-pipe registry continues to hold the underlying pipe, so incoming-data
/// routing is unaffected.
/// </summary>
internal sealed class TappedNexusDuplexPipe : INexusDuplexPipe
{
    private readonly INexusDuplexPipe _inner;
    private readonly TappingPipeReader _reader;
    private readonly TappingPipeWriter _writer;

    public PipeRecording Recording { get; }

    public TappedNexusDuplexPipe(INexusDuplexPipe inner)
    {
        _inner = inner;
        Recording = new PipeRecording();
        _reader = new TappingPipeReader(_inner.Input, Recording);
        _writer = new TappingPipeWriter(_inner.Output, Recording);
        _ = _inner.CompleteTask.ContinueWith(t =>
        {
            Recording.RecordCompletion(t.Exception?.GetBaseException());
        }, TaskScheduler.Default);
    }

    public PipeReader Input => _reader;
    public PipeWriter Output => _writer;
    public ushort Id => _inner.Id;
    public Task ReadyTask => _inner.ReadyTask;
    public Task CompleteTask => _inner.CompleteTask;
    public ValueTask CompleteAsync() => _inner.CompleteAsync();
    NexusPipeWriter INexusDuplexPipe.WriterCore => _inner.WriterCore;
    NexusPipeReader INexusDuplexPipe.ReaderCore => _inner.ReaderCore;
}

internal sealed class TappedRentedNexusDuplexPipe : IRentedNexusDuplexPipe
{
    private readonly IRentedNexusDuplexPipe _inner;
    private readonly TappingPipeReader _reader;
    private readonly TappingPipeWriter _writer;

    public PipeRecording Recording { get; }

    public TappedRentedNexusDuplexPipe(IRentedNexusDuplexPipe inner)
    {
        _inner = inner;
        Recording = new PipeRecording();
        _reader = new TappingPipeReader(_inner.Input, Recording);
        _writer = new TappingPipeWriter(_inner.Output, Recording);
        _ = _inner.CompleteTask.ContinueWith(t =>
        {
            Recording.RecordCompletion(t.Exception?.GetBaseException());
        }, TaskScheduler.Default);
    }

    public PipeReader Input => _reader;
    public PipeWriter Output => _writer;
    public ushort Id => _inner.Id;
    public Task ReadyTask => _inner.ReadyTask;
    public Task CompleteTask => _inner.CompleteTask;
    public ValueTask CompleteAsync() => _inner.CompleteAsync();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
    NexusPipeWriter INexusDuplexPipe.WriterCore => _inner.WriterCore;
    NexusPipeReader INexusDuplexPipe.ReaderCore => _inner.ReaderCore;
}
