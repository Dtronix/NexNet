using Avalonia.Controls;

namespace NexNet.Inspector.App.Platform;

/// <summary>Whether a window still has its extended client area; Unknown never leads to a re-apply.</summary>
internal enum ExtendedClientState : byte
{
    Unknown,
    Extended,
    Lost,
}

/// <summary>A rectangle in screen pixels.</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// Reads the extended client area from the platform window alone: Avalonia's <c>IsExtendedIntoWindowDecorations</c>
/// stays true after 12.1.3 drops the extension (it clears <c>_isClientAreaExtended</c> when DwmIsCompositionEnabled fails
/// mid-resize and raises no change).
/// </summary>
internal static class ExtendedClientArea
{
    private const int HtNowhere = 0;
    private const int HtCaption = 2;

    /// <summary>
    /// Normal: BorderOnly + extended insets the client by the frame at the sides and bottom and less at the top (1 px in
    /// 12.1.3), or not at all; lost, DefWindowProc insets it by the frame on all four sides. Maximized, both fill the work
    /// area, so <paramref name="topHit"/>, the window's WM_NCHITTEST answer in its top strip, decides: only the extended
    /// caption answers HTCAPTION there.
    /// </summary>
    public static ExtendedClientState Classify(ScreenRect window, ScreenRect client, WindowState state, ScreenRect workArea, int topHit)
    {
        if (window.Width <= 0 || window.Height <= 0 || client.Width <= 0 || client.Height <= 0) return ExtendedClientState.Unknown;
        int left = client.Left - window.Left, top = client.Top - window.Top, right = window.Right - client.Right, bottom = window.Bottom - client.Bottom;
        if (left < 0 || top < 0 || right < 0 || bottom < 0) return ExtendedClientState.Unknown;

        switch (state)
        {
            case WindowState.Normal:
                if (left == 0 && top == 0 && right == 0 && bottom == 0) return ExtendedClientState.Extended;
                if (left <= 0 || left != right || right != bottom) return ExtendedClientState.Unknown;
                return top == left ? ExtendedClientState.Lost : top < left ? ExtendedClientState.Extended : ExtendedClientState.Unknown;

            case WindowState.Maximized:
                // Mid-move across monitors the work area read may not be the one the window fills.
                if (workArea.Width <= 0 || workArea.Height <= 0) return ExtendedClientState.Unknown;
                bool covers = window.Left <= workArea.Left && window.Top <= workArea.Top && window.Right >= workArea.Right && window.Bottom >= workArea.Bottom;
                if (!covers || topHit == HtNowhere) return ExtendedClientState.Unknown;
                return topHit == HtCaption ? ExtendedClientState.Extended : ExtendedClientState.Lost;

            default:
                return ExtendedClientState.Unknown;
        }
    }
}
