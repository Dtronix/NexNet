using Microsoft.Extensions.Time.Testing;
using NexNet.Internals;

namespace NexNet.IntegrationTests;

internal class TimeProviderExtensionsTests
{
    [Test]
    public async Task GetTickCount64_FakeClockAtRealDate_IsPositiveAndAdvancesExactly()
    {
        // Timestamps of a clock set to today are ticks since year 1: multiplied by 1000 they overflow a long.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var start = time.GetTickCount64();
        await Assert.That(start).IsGreaterThan(0);

        time.Advance(TimeSpan.FromMilliseconds(1500));
        await Assert.That(time.GetTickCount64() - start).IsEqualTo(1500);

        time.Advance(TimeSpan.FromDays(400));
        await Assert.That(time.GetTickCount64() - start).IsEqualTo(1500 + 400L * 24 * 60 * 60 * 1000);
    }

    [Test]
    public async Task GetTickCount64_SystemClock_MatchesStopwatchMilliseconds()
    {
        var timestamp = TimeProvider.System.GetTimestamp();
        var ticks = TimeProvider.System.GetTickCount64();
        var expected = timestamp * 1000 / TimeProvider.System.TimestampFrequency;
        await Assert.That(ticks - expected).IsBetween(0, 1000);
    }
}