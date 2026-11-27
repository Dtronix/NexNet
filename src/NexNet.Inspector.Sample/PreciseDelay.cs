using System.Diagnostics;

namespace NexNet.Inspector.Sample;

/// <summary>A wait that keeps its length below the OS timer tick.</summary>
/// <remarks>
/// Task.Delay rounds up to the timer tick (about 15.6 ms on Windows), so the 3 ms pauses that pace the Chat scenario
/// would each last 15 ms or more. The coarse part of a wait sleeps; the last tick's worth yields until the deadline.
/// Sample and fixture code only: it keeps a thread pool thread busy for that last part.
/// </remarks>
public static class PreciseDelay
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);

    /// <summary>Waits <paramref name="delay"/>.</summary>
    public static async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay <= TimeSpan.Zero)
            return;
        var start = Stopwatch.GetTimestamp();
        if (delay > Tick + Tick)
            await Task.Delay(delay - Tick - Tick, cancellationToken).ConfigureAwait(false);
        while (Stopwatch.GetElapsedTime(start) < delay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
