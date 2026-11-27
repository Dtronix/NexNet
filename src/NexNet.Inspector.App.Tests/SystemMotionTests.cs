using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Platform;

namespace NexNet.Inspector.App.Tests;

/// <summary>
/// The OS motion preference (OQ12): Windows' SPI_GETCLIENTAREAANIMATION, a no-op that reports animations on elsewhere; a
/// failed read leaves animations on. The rail and the menus take their 80 ms motion from it.
/// </summary>
public sealed class SystemMotionTests
{
    [Test]
    [Arguments(true, 1, true)]
    [Arguments(true, 0, false)]
    [Arguments(false, 0, true)]
    [Arguments(false, 1, true)]
    public async Task A_read_turns_animations_off_only_when_it_succeeds_and_says_off(bool read, int value, bool expected) =>
        await Assert.That(SystemMotion.Interpret(read, value)).IsEqualTo(expected);

    [Test]
    public async Task Reduced_motion_is_the_inverse_of_shown_animations() =>
        await Assert.That(SystemMotion.ReducesMotion()).IsEqualTo(!SystemMotion.ShowsAnimations());

    [Test]
    public async Task Off_windows_the_read_is_a_no_op_that_shows_animations()
    {
        if (OperatingSystem.IsWindows()) Skip.Test("the Windows read is the system's own setting");
        await Assert.That(SystemMotion.ShowsAnimations()).IsTrue();
    }

    [Test]
    public async Task The_rail_and_menus_take_their_motion_from_the_preference()
    {
        bool shows = SystemMotion.ShowsAnimations();
        using (Assert.Multiple())
        {
            await Assert.That(ScrollRail.WidthMotion).IsEqualTo(RailScrollMath.WidthDuration(shows));
            await Assert.That(ScrollRail.ColourMotion).IsEqualTo(RailScrollMath.ColourDuration(shows));
            await Assert.That(MenuMotion.Fade).IsEqualTo(RailScrollMath.ColourDuration(shows));
        }
    }
}
