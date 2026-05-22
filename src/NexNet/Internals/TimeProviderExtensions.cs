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
    /// Wrap-around bound: ~29 years uptime at 10 MHz Stopwatch frequency, well beyond
    /// any realistic process lifetime. Fake providers (e.g. <c>FakeTimeProvider</c>) drive
    /// the value off their own timestamp without any wall-clock dependency.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long GetTickCount64(this TimeProvider time)
        => (time.GetTimestamp() * 1000L) / time.TimestampFrequency;
}
