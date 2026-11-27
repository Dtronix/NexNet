using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Tests.Documents;

public class SyncBusTests
{
    [Test]
    public async Task Many_changes_between_two_frames_notify_once_with_every_kind()
    {
        var ui = new QueuedUi();
        var bus = new SyncBus(ui);
        var notified = new List<SyncChanges>();
        bus.Changed += notified.Add;

        for (var i = 0; i < 100; i++)
            bus.SetWindow(new TimeWindow(i, i + 1000));
        bus.SetPlayhead(42);
        bus.SetHover(new FieldHover(default, 3, 10, 4));

        await Assert.That(ui.Pending).IsEqualTo(1);
        ui.RunAll();
        await Assert.That(notified).IsEquivalentTo(new[] { SyncChanges.Window | SyncChanges.Playhead | SyncChanges.Hover });
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(99, 1099));

        // Nothing new: no notification.
        bus.SetPlayhead(42);
        await Assert.That(ui.Pending).IsEqualTo(0);
    }

    [Test]
    public async Task Selecting_moves_the_playhead()
    {
        var ui = new QueuedUi();
        var bus = new SyncBus(ui);
        var selection = Selection.OfFrame(new FrameKey(TapDirection.Inbound, 7));
        bus.Select(selection, ticks: 5000);
        ui.RunAll();
        await Assert.That(bus.Selection).IsEqualTo(selection);
        await Assert.That(bus.Playhead).IsEqualTo(5000);
    }

    [Test]
    public async Task Following_live_slides_the_window_until_the_user_pans_back()
    {
        var bus = new SyncBus(new QueuedUi());
        bus.SetWindow(new TimeWindow(0, 100), byUser: false);
        bus.SetFollowLive(true);
        bus.OnLiveData(250);
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(150, 250));

        bus.Pan(-50);
        await Assert.That(bus.FollowLive).IsFalse();
        bus.OnLiveData(400);
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(100, 200));
    }

    [Test]
    public async Task The_window_stays_inside_the_bounds()
    {
        var bus = new SyncBus(new QueuedUi());
        bus.SetBounds(new TimeWindow(1000, 2000));
        bus.SetWindow(new TimeWindow(1900, 2100));
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(1800, 2000));
        bus.SetWindow(new TimeWindow(500, 600));
        await Assert.That(bus.Window).IsEqualTo(new TimeWindow(1000, 1100));
    }
}
