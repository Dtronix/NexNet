using Microsoft.Extensions.Time.Testing;
using NexNet.Internals;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class TimeProviderExtensionsTests
{
    [Test]
    public void GetTickCount64_FakeClockAtRealDate_IsPositiveAndAdvancesExactly()
    {
        // Timestamps of a clock set to today are ticks since year 1: multiplied by 1000 they overflow a long.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var start = time.GetTickCount64();
        Assert.That(start, Is.GreaterThan(0));

        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.That(time.GetTickCount64() - start, Is.EqualTo(1500));

        time.Advance(TimeSpan.FromDays(400));
        Assert.That(time.GetTickCount64() - start, Is.EqualTo(1500 + 400L * 24 * 60 * 60 * 1000));
    }

    [Test]
    public void GetTickCount64_SystemClock_MatchesStopwatchMilliseconds()
    {
        var timestamp = TimeProvider.System.GetTimestamp();
        var ticks = TimeProvider.System.GetTickCount64();
        var expected = timestamp * 1000 / TimeProvider.System.TimestampFrequency;
        Assert.That(ticks - expected, Is.InRange(0, 1000));
    }
}
