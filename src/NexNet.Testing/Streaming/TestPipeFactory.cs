using System.Collections.Concurrent;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Pipes;
using NexNet.Testing.Quiescence;

namespace NexNet.Testing.Streaming;

/// <summary>
/// Harness implementation of <see cref="IPipeFactory"/>. Wraps every pipe handed to user code
/// with a <see cref="TappedNexusDuplexPipe"/> / <see cref="TappedRentedNexusDuplexPipe"/>,
/// registers the resulting <see cref="PipeRecording"/> by id for later lookup, and brackets
/// each pipe's lifetime with <c>OpenPipe</c>/<c>ClosePipe</c> on the shared counters.
/// </summary>
internal sealed class TestPipeFactory : IPipeFactory
{
    private readonly QuiescenceCounters _counters;
    private readonly QuiescenceTracker _tracker;
    private readonly ConcurrentDictionary<ushort, PipeRecording> _byId = new();

    public TestPipeFactory(QuiescenceCounters counters, QuiescenceTracker tracker)
    {
        _counters = counters;
        _tracker = tracker;
    }

    public IRentedNexusDuplexPipe WrapLocal(IRentedNexusDuplexPipe inner)
    {
        // We deliberately do NOT wrap the locally-rented pipe: the framework's proxy invoker
        // does an Unsafe.As<NexusDuplexPipe>(pipe) when reading the initial id (see
        // ProxyInvocationBase.ProxyGetDuplexPipeInitialId), so a wrapper that merely implements
        // INexusDuplexPipe would read garbage memory through the cast. Locally-rented pipes
        // therefore use the byte-level transport tap (via BytesInTransit) for quiescence and
        // give up the per-pipe-recording observation API on the caller side. Remote (incoming)
        // pipes are still wrapped — see WrapRemote.
        Track(inner.CompleteTask, recording: null);
        return inner;
    }

    public INexusDuplexPipe WrapRemote(INexusDuplexPipe inner)
    {
        var wrapped = new TappedNexusDuplexPipe(inner);
        _byId.TryAdd(inner.Id, wrapped.Recording);
        Track(inner.CompleteTask, wrapped.Recording);
        return wrapped;
    }

    private void Track(Task completeTask, PipeRecording? recording)
    {
        // `recording` is ignored here today; the bracketed Open/Close pair is what quiescence
        // needs. Keeping the parameter so callers can stay declarative ("this pipe owns this
        // recording") even when the recording is attached via a different code path.
        _ = recording;
        _counters.OpenPipe();
        _tracker.SignalChange();
        _ = completeTask.ContinueWith(_ =>
        {
            _counters.ClosePipe();
            _tracker.SignalChange();
        }, TaskScheduler.Default);
    }

    /// <summary>Looks up the recording for a remote-registered pipe by id, if any.</summary>
    public PipeRecording? GetRecordingFor(ushort pipeId)
        => _byId.TryGetValue(pipeId, out var rec) ? rec : null;
}
