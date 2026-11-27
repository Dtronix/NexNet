using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NexNet.Inspector.App.Platform;

/// <summary>A rectangle in screen pixels, as Win32 gives it.</summary>
internal readonly record struct FrameRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Reads a top-level window's frame from Win32: its window rect, its client rect in screen coordinates and its monitor's
/// work area; its own WM_NCHITTEST answer at a point; and its session-change notifications. Each method answers false
/// or 0 when the call fails or off Windows. Source-generated P/Invoke, so Native AOT needs no marshalling stubs.
/// </summary>
internal static partial class WindowFrame
{
    private const uint WmNcHitTest = 0x0084;
    private const uint MonitorDefaultToNearest = 2;
    private const uint NotifyForThisSession = 0;

    /// <summary>The window rect, the client rect (moved to screen coordinates) and the work area of the nearest monitor.</summary>
    public static bool TryRead(nint hwnd, out FrameRect window, out FrameRect client, out FrameRect workArea)
    {
        window = client = workArea = default;
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        if (GetWindowRect(hwnd, out Rect w) == 0 || GetClientRect(hwnd, out Rect c) == 0) return false;
        Point origin = default;
        if (ClientToScreen(hwnd, ref origin) == 0) return false;
        window = new FrameRect(w.Left, w.Top, w.Right, w.Bottom);
        client = new FrameRect(origin.X + c.Left, origin.Y + c.Top, origin.X + c.Right, origin.Y + c.Bottom);

        // No work area is not a failure: the caller decides what a window without one means.
        nint monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        MonitorInfo info = new() { Size = (uint)Unsafe.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfoW(monitor, ref info) != 0) workArea = new FrameRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        return true;
    }

    /// <summary>The window's own WM_NCHITTEST answer at a screen point (HTNOWHERE, 0, when it cannot be asked).</summary>
    public static int HitTest(nint hwnd, int x, int y)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return 0;

        // MAKELPARAM: each coordinate is a signed 16-bit word, so a point left of or above the primary monitor survives.
        nint lParam = (int)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);
        return (int)SendMessageW(hwnd, WmNcHitTest, 0, lParam);
    }

    /// <summary>Asks for WM_WTSSESSION_CHANGE (lock, unlock, remote connect) for this session.</summary>
    public static bool RegisterSessionNotification(nint hwnd) =>
        OperatingSystem.IsWindows() && hwnd != 0 && WTSRegisterSessionNotification(hwnd, NotifyForThisSession) != 0;

    public static void UnregisterSessionNotification(nint hwnd)
    {
        if (OperatingSystem.IsWindows() && hwnd != 0) _ = WTSUnRegisterSessionNotification(hwnd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int ClientToScreen(nint hwnd, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("wtsapi32.dll")]
    private static partial int WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    private static partial int WTSUnRegisterSessionNotification(nint hwnd);
}
