using System.Collections.Concurrent;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Pipes;
using NexNet.Testing.Quiescence;

namespace NexNet.Testing.Streaming;

/// <summary>
/// Harness implementation of <see cref="IPipeFactory"/>. Remote (incoming) pipes handed to user
/// code are wrapped with a <see cref="TappedNexusDuplexPipe"/> and their <see cref="PipeRecording"/>
/// registered by id for later lookup; locally-rented pipes pass through unwrapped (see
/// <see cref="WrapLocal"/> for why). Either way each pipe's lifetime is bracketed with
/// <c>OpenPipe</c>/<c>ClosePipe</c> on the shared counters.
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
        // pipes are still wrapped — see WrapRemote. The id-keying for `GetRecordingFor` is
        // intentionally only populated by WrapRemote (where the id is known and stable at
        // wrap-time); locally-rented pipes start at Id=0 and aren't lookup-able by id.
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
        _counters.OpenPipe();
        _tracker.SignalChange();
        _ = completeTask.ContinueWith(t =>
        {
            // Drive both the quiescence bookkeeping and the recording's completion signal from
            // the single continuation. Previously the wrapper registered its own
            // RecordCompletion continuation in parallel; consolidating here removes the duplicate
            // callback path and the constructor-time race window it implied.
            recording?.RecordCompletion(t.Exception?.GetBaseException());
            _counters.ClosePipe();
            _tracker.SignalChange();
        }, TaskScheduler.Default);
    }

    /// <summary>Looks up the recording for a remote-registered pipe by id, if any.</summary>
    public PipeRecording? GetRecordingFor(ushort pipeId)
        => _byId.TryGetValue(pipeId, out var rec) ? rec : null;
}
