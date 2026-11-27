using Avalonia.Animation.Easings;
using NexNet.Inspector.App.Platform;

namespace NexNet.Inspector.App.Controls;

/// <summary>
/// The menu and tooltip surfaces' appearance (Themes/Menus.axaml): an opacity fade over 120 ms, 80 ms when the OS's
/// animations are off, eased by cubic-bezier(.2,0,0,1). Never a brush fade: from Transparent it draws light greys mid-way.
/// </summary>
public static class MenuMotion
{
    /// <summary>The fade's length, from the OS preference read once.</summary>
    public static TimeSpan Fade { get; } = RailScrollMath.ColourDuration(SystemMotion.ShowsAnimations());

    /// <summary>The design's ease, the rail's.</summary>
    public static Easing Ease => ScrollRail.Ease;
}
