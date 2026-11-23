using System.Diagnostics;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Controls.Timeline;
using static NexNet.Inspector.App.Tests.Timeline.TimelineDataBuilder;

namespace NexNet.Inspector.App.Tests.Timeline;

/// <summary>OQ12: a lane added or removed moves its height over 160 ms with the standard ease, no fade; at once when the OS reduces motion.</summary>
public class LaneAnimationTests
{
    private static long At(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    [Test]
    public async Task A_new_lane_grows_over_160ms_with_the_standard_ease()
    {
        LaneAnimator animator = new(enabled: true);
        LaneKey key = new(LaneKind.Pipe, 1);
        await Assert.That(animator.Height(key, 12, At(1000))).IsEqualTo(0);
        await Assert.That(animator.IsAnimating(At(1000))).IsTrue();

        double half = animator.Height(key, 12, At(1080));
        await Assert.That(half).IsEqualTo(12 * ScrollRail.Ease.Ease(0.5)).Within(1e-9);

        // cubic-bezier(.2,0,0,1) is front-loaded: past half height at half time.
        await Assert.That(half).IsGreaterThan(6);
        await Assert.That(animator.Height(key, 12, At(1160))).IsEqualTo(12);
        await Assert.That(animator.IsAnimating(At(1161))).IsFalse();
    }

    [Test]
    public async Task A_new_target_starts_from_where_the_lane_is()
    {
        LaneAnimator animator = new(enabled: true);
        LaneKey key = new(LaneKind.Reads, 0);
        animator.Height(key, 12, 0, instant: true);
        await Assert.That(animator.Height(key, 12, At(10))).IsEqualTo(12);

        // Expanding, then removed halfway: the removal starts at the height reached.
        animator.Height(key, 24, At(100));
        double reached = animator.Height(key, 24, At(180));
        double next = animator.Height(key, 0, At(180));
        await Assert.That(next).IsEqualTo(reached).Within(1e-9);
        await Assert.That(animator.Height(key, 0, At(340))).IsEqualTo(0);
    }

    [Test]
    public async Task Reduced_motion_applies_heights_at_once()
    {
        LaneAnimator animator = new(enabled: false);
        LaneKey key = new(LaneKind.Pipe, 1);
        await Assert.That(animator.Height(key, 12, 0)).IsEqualTo(12);
        await Assert.That(animator.Height(key, 24, 1)).IsEqualTo(24);
        await Assert.That(animator.IsAnimating(1)).IsFalse();
    }

    [Test]
    public async Task A_pipe_entering_the_window_animates_in_after_the_first_layout()
    {
        var model = Demo(pinned: false);
        TimelineLayout layout = new(new LaneAnimator(enabled: true));
        layout.Update(model, Window(30, 60), null, At(0), instant: true);
        await Assert.That(layout.IsAnimating).IsFalse();
        double before = layout.Height;

        // Pipe 5·1 opens at 73 ms: scrolling to 70-100 adds it (and drops nothing that animates away at once).
        layout.Update(model, Window(70, 100), null, At(1000));
        await Assert.That(layout.IsAnimating).IsTrue();
        layout.Update(model, Window(70, 100), null, At(1080));
        LayoutRow growing = layout.Rows.ToArray().Single(r => r.Lane?.Key == new LaneKey(LaneKind.Pipe, 5));
        await Assert.That(growing.Height).IsGreaterThan(0).And.IsLessThan(TimelineLayout.LaneHeight);

        layout.Update(model, Window(70, 100), null, At(1200));
        await Assert.That(layout.IsAnimating).IsFalse();
        growing = layout.Rows.ToArray().Single(r => r.Lane?.Key == new LaneKey(LaneKind.Pipe, 5));
        await Assert.That(growing.Height).IsEqualTo(TimelineLayout.LaneHeight);
        await Assert.That(layout.Height).IsNotEqualTo(before);
    }
}
