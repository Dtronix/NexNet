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
        var wrapped = new TappedRentedNexusDuplexPipe(inner);
        Track(inner.CompleteTask, wrapped.Recording);
        // Id is set to 0 on rent; the wrapper exposes it dynamically once the partner state
        // notification arrives. We use the recording reference directly for now since the
        // assertions look up by reference, not id, for locally-rented pipes.
        return wrapped;
    }

    public INexusDuplexPipe WrapRemote(INexusDuplexPipe inner)
    {
        var wrapped = new TappedNexusDuplexPipe(inner);
        _byId.TryAdd(inner.Id, wrapped.Recording);
        Track(inner.CompleteTask, wrapped.Recording);
        return wrapped;
    }

    private void Track(Task completeTask, PipeRecording recording)
    {
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
