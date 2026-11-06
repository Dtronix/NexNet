using System;
using System.Runtime.CompilerServices;

namespace NexNet.Internals;

internal static class TimeProviderExtensions
{
    /// <summary>
    /// Milliseconds since an arbitrary fixed reference for the supplied <see cref="TimeProvider"/>.
    /// Drop-in replacement for <see cref="Environment.TickCount64"/> that honors the configured
    /// time source. Monotonic within a given provider instance.
    /// </summary>
    /// <remarks>
    /// On <see cref="TimeProvider.System"/> this is Stopwatch-backed (~15-25 ns/call).
    /// Fake providers (e.g. <c>FakeTimeProvider</c>) drive the value off their own timestamp
    /// without any wall-clock dependency; one set to a real date (timestamps near 2^60) does not overflow.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetTickCount64(this TimeProvider time)
    {
        var timestamp = time.GetTimestamp();
        var frequency = time.TimestampFrequency;
        // Stopwatch timestamps are small: one multiply and divide. Split only when the multiplication would
        // overflow, as for a fake clock set to a real date (timestamps near 2^60).
        return timestamp <= long.MaxValue / 1000
            ? timestamp * 1000L / frequency
            : timestamp / frequency * 1000L + timestamp % frequency * 1000L / frequency;
    }
}
