using System.Threading;

namespace NexNet.Testing.Quiescence;

/// <summary>
/// Per-session counter set tracked by the harness. Atomic increments/decrements; reads use a
/// volatile load to pick up the latest value. A <see cref="QuiescenceTracker"/> owns one
/// instance per active session and aggregates them when answering <c>QuiesceAsync</c>.
/// </summary>
internal sealed class QuiescenceCounters
{
    private int _bytesInTransit;
    private int _inDispatch;
    private int _activePipes;

    public int BytesInTransit => Volatile.Read(ref _bytesInTransit);
    public int InDispatch => Volatile.Read(ref _inDispatch);
    public int ActivePipes => Volatile.Read(ref _activePipes);

    public void AddBytesInTransit(int delta) => Interlocked.Add(ref _bytesInTransit, delta);
    public void EnterDispatch() => Interlocked.Increment(ref _inDispatch);
    public void ExitDispatch() => Interlocked.Decrement(ref _inDispatch);
    public void OpenPipe() => Interlocked.Increment(ref _activePipes);
    public void ClosePipe() => Interlocked.Decrement(ref _activePipes);
}
