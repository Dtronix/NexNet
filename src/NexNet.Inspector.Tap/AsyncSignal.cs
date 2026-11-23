using System.Threading.Tasks.Sources;

namespace NexNet.Inspector.Tap;

/// <summary>
/// A reusable one-waiter signal: <see cref="Arm"/> returns a task that completes on the next <see cref="Set"/>,
/// with no allocation per wait.
/// </summary>
/// <remarks>
/// The owner guarantees the protocol under its own lock: one <see cref="Arm"/>, then exactly one
/// <see cref="Set"/>, and the returned task awaited once before the next <see cref="Arm"/>. Continuations run
/// asynchronously so <see cref="Set"/> never runs the waiter's code on the setter's thread (a session's read loop).
/// </remarks>
internal sealed class AsyncSignal : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };

    /// <summary>Starts a wait.</summary>
    public ValueTask Arm()
    {
        _core.Reset();
        return new ValueTask(this, _core.Version);
    }

    /// <summary>Completes the armed wait.</summary>
    public void Set() => _core.SetResult(true);

    void IValueTaskSource.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);
}
