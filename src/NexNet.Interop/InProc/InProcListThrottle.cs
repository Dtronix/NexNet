namespace NexNet.Interop.InProc;

/// <summary>
/// A <see cref="RemoteListThrottle"/> of a C# client's list: its synchronous <c>ProcessTestHook</c>, which blocks the
/// list's processing (and so its reading of the collection pipe) while it delays or holds a message.
/// </summary>
/// <param name="throttle">The throttle.</param>
internal sealed class InProcListThrottle(RemoteListThrottle throttle)
{
    /// <summary>The longest a <see cref="RemoteListThrottle.Pause"/> holds a message, so a failed test never leaves a reader stalled.</summary>
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);

    private readonly ManualResetEventSlim _release = new();
    private int _seen;
    private volatile bool _holding;
    private volatile bool _stopped;

    /// <summary>What the throttle has seen.</summary>
    public RemoteListThrottleState State => new(Volatile.Read(ref _seen), _holding);

    /// <summary>Called before the list processes each received message.</summary>
    public void Hook()
    {
        var n = Interlocked.Increment(ref _seen);
        if (_stopped)
            return;
        if (n == 1 && throttle.Pause)
        {
            _holding = true;
            try
            {
                _release.Wait(MaxHold);
            }
            finally
            {
                _holding = false;
            }
        }
        else if (n == 1 && throttle.FirstDelayMs > 0)
        {
            Thread.Sleep(throttle.FirstDelayMs);
        }
        else if (throttle.DelayMs > 0 && n % throttle.Every == 0)
        {
            Thread.Sleep(throttle.DelayMs);
        }
    }

    /// <summary>Releases a held message; later messages are neither held nor delayed.</summary>
    public void Stop()
    {
        _stopped = true;
        _release.Set();
    }
}
