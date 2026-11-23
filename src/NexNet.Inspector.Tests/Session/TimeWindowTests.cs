using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Tests.Session;

public class TimeWindowTests
{
    [Test]
    public async Task Zoom_keeps_the_anchor_in_place()
    {
        var window = new TimeWindow(1000, 2000);
        var zoomed = window.ZoomAround(1250, 0.5);
        await Assert.That(zoomed).IsEqualTo(new TimeWindow(1125, 1625));
        await Assert.That(zoomed.ToPixel(1250, 800)).IsEqualTo(window.ToPixel(1250, 800));

        var out2 = window.ZoomAround(2000, 2);
        await Assert.That(out2).IsEqualTo(new TimeWindow(0, 2000));
        await Assert.That(window.ZoomAround(1500, 1e-9).Duration).IsEqualTo(TimeWindow.MinDuration);
        await Assert.That(() => window.ZoomAround(1500, 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Pixels_map_both_ways()
    {
        var window = new TimeWindow(10_000, 20_000);
        await Assert.That(window.ToPixel(15_000, 1000)).IsEqualTo(500.0);
        await Assert.That(window.FromPixel(250, 1000)).IsEqualTo(12_500);
        await Assert.That(window.TicksPerPixel(1000)).IsEqualTo(10.0);
        await Assert.That(window.PixelsToTicks(-30, 1000)).IsEqualTo(-300);
        await Assert.That(window.Pan(-300)).IsEqualTo(new TimeWindow(9_700, 19_700));
    }

    [Test]
    public async Task Clamp_shifts_back_inside_and_narrows_to_the_bounds()
    {
        await Assert.That(new TimeWindow(-50, 50).Clamp(0, 1000)).IsEqualTo(new TimeWindow(0, 100));
        await Assert.That(new TimeWindow(950, 1050).Clamp(0, 1000)).IsEqualTo(new TimeWindow(900, 1000));
        await Assert.That(new TimeWindow(-10, 5000).Clamp(0, 1000)).IsEqualTo(new TimeWindow(0, 1000));
        await Assert.That(new TimeWindow(10, 20).Clamp(0, 1000)).IsEqualTo(new TimeWindow(10, 20));
    }

    [Test]
    public async Task Follow_and_rebase_keep_the_zoom()
    {
        var window = new TimeWindow(100, 150);
        await Assert.That(window.Follow(1000)).IsEqualTo(new TimeWindow(950, 1000));

        // 10 MHz to 1 GHz: offset and duration carry over in seconds.
        var rebased = new TimeWindow(1_000 + 10_000, 1_000 + 20_000).Rebase(1_000, 10_000_000, 5_000_000, 1_000_000_000);
        await Assert.That(rebased).IsEqualTo(new TimeWindow(5_000_000 + 1_000_000, 5_000_000 + 2_000_000));
        await Assert.That(TimeWindow.TicksFromMilliseconds(50, 10_000_000)).IsEqualTo(500_000);
        await Assert.That(TimeWindow.MillisecondsFromTicks(500_000, 10_000_000)).IsEqualTo(50.0);
    }
}
