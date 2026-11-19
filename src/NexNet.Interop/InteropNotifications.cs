using NexNet.TestNexus.Interop;

namespace NexNet.Interop;

/// <summary>Awaitable assertions on the fire-and-forget notifications of <see cref="IInteropServerNexus"/>.</summary>
/// <remarks>
/// <see cref="IInteropServerNexus.Notify"/> returns nothing and the server runs invocations concurrently, so neither
/// <see cref="IClientUnderTest.BarrierAsync"/> (the client has sent it) nor a later call on the same session guarantees
/// the server has recorded the notification when <see cref="IInteropServerNexus.TakeNotifications"/> runs. Poll instead.
/// </remarks>
public static class InteropNotifications
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Takes notifications from the server until at least <paramref name="count"/> have arrived or
    /// <paramref name="timeout"/> elapses, and returns every one taken, in arrival order. On timeout it returns what it
    /// has (fewer than <paramref name="count"/>), so the caller's assertion reports the shortfall.
    /// </summary>
    public static async Task<string[]> WaitNotificationsAsync(this IInteropServerNexus proxy, int count, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var taken = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            taken.AddRange(await proxy.TakeNotifications().AsTask().WaitAsync(timeout).ConfigureAwait(false));
            if (taken.Count >= count || DateTime.UtcNow >= deadline)
                return taken.ToArray();
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }
}
