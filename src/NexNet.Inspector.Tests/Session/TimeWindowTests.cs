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
    public async Task Resizing_an_edge_keeps_the_other_a_minimum_span_away_and_inside_the_bounds()
    {
        var window = new TimeWindow(200, 600);
        var bounds = new TimeWindow(0, 1000);
        await Assert.That(window.WithStart(100, 10, bounds)).IsEqualTo(new TimeWindow(100, 600));
        await Assert.That(window.WithStart(-50, 10, bounds)).IsEqualTo(new TimeWindow(0, 600));
        await Assert.That(window.WithStart(900, 10, bounds)).IsEqualTo(new TimeWindow(590, 600));
        await Assert.That(window.WithEnd(800, 10, bounds)).IsEqualTo(new TimeWindow(200, 800));
        await Assert.That(window.WithEnd(5000, 10, bounds)).IsEqualTo(new TimeWindow(200, 1000));
        await Assert.That(window.WithEnd(0, 10, bounds)).IsEqualTo(new TimeWindow(200, 210));

        // No bounds: only the minimum span (never under one tick) holds.
        await Assert.That(window.WithStart(-50, 0, default)).IsEqualTo(new TimeWindow(-50, 600));
        await Assert.That(window.WithEnd(200, 0, default)).IsEqualTo(new TimeWindow(200, 201));
    }

    [Test]
    public async Task Pan_within_keeps_the_span_inside_the_bounds()
    {
        var bounds = new TimeWindow(0, 1000);
        await Assert.That(new TimeWindow(200, 600).PanWithin(100, bounds)).IsEqualTo(new TimeWindow(300, 700));
        await Assert.That(new TimeWindow(200, 600).PanWithin(-500, bounds)).IsEqualTo(new TimeWindow(0, 400));
        await Assert.That(new TimeWindow(200, 600).PanWithin(900, bounds)).IsEqualTo(new TimeWindow(600, 1000));
        // As wide as the bounds or wider (a live window ahead of its data): it stays.
        await Assert.That(new TimeWindow(0, 1500).PanWithin(100, bounds)).IsEqualTo(new TimeWindow(0, 1500));
        await Assert.That(new TimeWindow(200, 600).PanWithin(-500, default)).IsEqualTo(new TimeWindow(-300, 100));
    }

    [Test]
    public async Task Between_orders_widens_to_the_minimum_span_and_clamps()
    {
        var bounds = new TimeWindow(0, 1000);
        await Assert.That(TimeWindow.Between(700, 300, 10, bounds)).IsEqualTo(new TimeWindow(300, 700));
        await Assert.That(TimeWindow.Between(-100, 300, 10, bounds)).IsEqualTo(new TimeWindow(0, 300));
        await Assert.That(TimeWindow.Between(500, 502, 10, bounds)).IsEqualTo(new TimeWindow(496, 506));
        await Assert.That(TimeWindow.Between(998, 999, 10, bounds)).IsEqualTo(new TimeWindow(990, 1000));
        await Assert.That(TimeWindow.Between(5, 5, 4000, bounds)).IsEqualTo(bounds);

        // 10 µs at 1 MHz, 10 MHz and TimeSpan ticks; never under one tick.
        await Assert.That(TimeWindow.MinSpan(1_000_000)).IsEqualTo(10);
        await Assert.That(TimeWindow.MinSpan(10_000_000)).IsEqualTo(100);
        await Assert.That(TimeWindow.MinSpan(TimeSpan.TicksPerSecond)).IsEqualTo(100);
        await Assert.That(TimeWindow.MinSpan(1)).IsEqualTo(1);
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
