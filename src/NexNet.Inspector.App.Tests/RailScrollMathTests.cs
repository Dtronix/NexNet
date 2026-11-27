using NexNet.Inspector.App.Controls;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// The rail scrollbar's arithmetic: the thumb's length (never under 28 px), a track page (0.9 × viewport toward the pointer),
/// a Shift+press jump (the thumb centred on the pointer), a thumb drag (Δ × max ÷ (track − thumb)), each clamped to [0, max];
/// the 16 px proximity zone; the motion durations (160 / 120 ms, 80 ms when the OS's animations are off) and the ease-out.
/// </summary>
public sealed class RailScrollMathTests
{
    [Test]
    public async Task TheConstantsAreTheDesigns()
    {
        using (Assert.Multiple())
        {
            await Assert.That((RailScrollMath.RestWidth, RailScrollMath.NearWidth, RailScrollMath.ThumbInset, RailScrollMath.ProximityZone)).IsEqualTo((2, 12, 2, 16));
            await Assert.That((RailScrollMath.MinThumb, RailScrollMath.InsetX, RailScrollMath.InsetY, RailScrollMath.PageFraction)).IsEqualTo((28, 2, 3, 0.9));
        }
    }

    [Test]
    [Arguments(200, 400, 190, 95)]
    [Arguments(200, 2000, 190, 28)]
    [Arguments(200, 100_000, 190, 28)]
    [Arguments(200, 200, 190, 190)]
    [Arguments(10, 2000, 20, 20)]
    public async Task TheThumbIsViewportOverExtentOfTheTrackAndNeverUnder28Px(double viewport, double extent, double track, double expected) =>
        await Assert.That(RailScrollMath.ThumbLength(viewport, extent, track)).IsEqualTo(expected).Within(1e-9);

    [Test]
    [Arguments(0, 200, 1800, +1, 180)]
    [Arguments(500, 200, 1800, -1, 320)]
    [Arguments(100, 200, 1800, -1, 0)]
    [Arguments(1750, 200, 1800, +1, 1800)]
    [Arguments(0, 200, 1800, -1, 0)]
    public async Task APageIsNineTenthsOfTheViewportClampedToTheRange(double offset, double viewport, double max, int direction, double expected) =>
        await Assert.That(RailScrollMath.PageTarget(offset, viewport, max, direction)).IsEqualTo(expected).Within(1e-9);

    [Test]
    [Arguments(95, 190, 28, 1800, 900)]
    [Arguments(14, 190, 28, 1800, 0)]
    [Arguments(176, 190, 28, 1800, 1800)]
    [Arguments(0, 190, 28, 1800, 0)]
    [Arguments(190, 190, 28, 1800, 1800)]
    [Arguments(50, 190, 190, 1800, 0)]
    public async Task AJumpCentresTheThumbOnThePointerClampedToTheRange(double along, double track, double thumb, double max, double expected) =>
        await Assert.That(RailScrollMath.JumpTarget(along, track, thumb, max)).IsEqualTo(expected).Within(1e-9);

    [Test]
    [Arguments(0, 81, 1800, 190, 28, 900)]
    [Arguments(900, -81, 1800, 190, 28, 0)]
    [Arguments(900, 500, 1800, 190, 28, 1800)]
    [Arguments(900, -500, 1800, 190, 28, 0)]
    [Arguments(0, 50, 1800, 190, 190, 0)]
    public async Task ADragMovesTheOffsetByDeltaTimesMaxOverTheThumbsTravelClamped(double start, double delta, double max, double track, double thumb, double expected) =>
        await Assert.That(RailScrollMath.DragOffset(start, delta, max, track, thumb)).IsEqualTo(expected).Within(1e-9);

    [Test]
    [Arguments(184, 200, true)]
    [Arguments(199, 200, true)]
    [Arguments(200, 200, true)]
    [Arguments(183.9, 200, false)]
    [Arguments(10, 200, false)]
    public async Task TheProximityZoneIsTheLast16PxOfTheViewport(double position, double size, bool expected) =>
        await Assert.That(RailScrollMath.InZone(position, size)).IsEqualTo(expected);

    [Test]
    public async Task TheMotionIs160And120MsOr80MsWhenAnimationsAreOff()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RailScrollMath.WidthDuration(showsAnimations: true)).IsEqualTo(TimeSpan.FromMilliseconds(160));
            await Assert.That(RailScrollMath.ColourDuration(showsAnimations: true)).IsEqualTo(TimeSpan.FromMilliseconds(120));
            await Assert.That(RailScrollMath.ScrollDuration(showsAnimations: true)).IsEqualTo(TimeSpan.FromMilliseconds(160));
            await Assert.That(RailScrollMath.WidthDuration(showsAnimations: false)).IsEqualTo(TimeSpan.FromMilliseconds(80));
            await Assert.That(RailScrollMath.ColourDuration(showsAnimations: false)).IsEqualTo(TimeSpan.FromMilliseconds(80));
            await Assert.That(RailScrollMath.ScrollDuration(showsAnimations: false)).IsEqualTo(TimeSpan.FromMilliseconds(80));
        }
    }

    /// <summary>cubic-bezier(.2,0,0,1): starts at 0, ends at 1, never leaves [0, 1], and is past half-way at half time.</summary>
    [Test]
    public async Task TheEaseIsTheDesignsEaseOut()
    {
        double[] samples = [.. Enumerable.Range(0, 21).Select(i => RailScrollMath.EaseOut(i / 20.0))];
        using (Assert.Multiple())
        {
            await Assert.That(RailScrollMath.EaseOut(0)).IsEqualTo(0).Within(1e-6);
            await Assert.That(RailScrollMath.EaseOut(1)).IsEqualTo(1).Within(1e-6);
            await Assert.That(RailScrollMath.EaseOut(0.5)).IsGreaterThan(0.75);
            await Assert.That(samples).IsInOrder();
            await Assert.That(samples).All(i => i >= 0.0 && i <= 1.0);
        }
    }
}
