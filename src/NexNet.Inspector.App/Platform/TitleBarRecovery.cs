using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace NexNet.Inspector.App.Platform;

/// <summary>The window the recovery watches: its state, the re-apply, and the messages after which it may have changed.</summary>
internal interface ITitleBarWindow : IDisposable
{
    ExtendedClientState Read();

    /// <summary>Sets the extended-client-area hint false, then true.</summary>
    void Reapply();

    event EventHandler? Changed;
}

/// <summary>Runs work once after a delay on the UI thread; disposing the result cancels it.</summary>
internal interface ITitleBarScheduler
{
    IDisposable Schedule(TimeSpan delay, Action action);

    /// <summary>A monotonic time, for how long a loss lasted.</summary>
    TimeSpan Now { get; }
}

/// <summary>
/// Puts the main window's custom title bar back when a display or session change drops it: Avalonia 12.1.3 clears its
/// extended client area when DwmIsCompositionEnabled fails during a resize and never sets it again. A window message
/// (re)starts a 250 ms debounce, then one check. Lost re-applies the hint and re-checks after 0.5, 1, 2, 4 s, then every
/// 10 s, until Extended; window messages are ignored while a re-check is pending. A loss writes no Warning: one Info line
/// when it ends, naming its attempts and how long it lasted, or at dispose if it is still going on. No poll outside a loss.
/// </summary>
internal sealed class TitleBarRecovery : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan[] Ladder = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    private readonly ITitleBarWindow _window;
    private readonly ITitleBarScheduler _scheduler;
    private readonly Action<string> _log;
    private IDisposable? _pending;
    private bool _inLoss;
    private int _attempts;
    private TimeSpan _lossStart;
    private int _step;
    private bool _disposed;

    private TitleBarRecovery(ITitleBarWindow window, ITitleBarScheduler scheduler, Action<string> log)
    {
        _window = window;
        _scheduler = scheduler;
        _log = log;
        _window.Changed += OnChanged;
    }

    /// <summary>Off Windows nothing is made or scheduled (the seam hooks the Win32 window procedure).</summary>
    public static TitleBarRecovery? Start(bool isWindows, Func<ITitleBarWindow> windowFactory, ITitleBarScheduler scheduler, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(windowFactory);
        return isWindows ? new TitleBarRecovery(windowFactory(), scheduler, log) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_inLoss)
        {
            _inLoss = false;
            try
            {
                _log($"TITLE BAR: the extended client area was still lost when the App closed, after {Describe()}");
            }
            catch (Exception)
            {
                // The log may already be closing with the app; the rest of Dispose must still run.
            }
        }

        _window.Changed -= OnChanged;
        _pending?.Dispose();
        _pending = null;
        _window.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        // A re-check is already coming; a message must not shorten the back-off.
        if (_disposed || _inLoss) return;
        _pending?.Dispose();
        _pending = _scheduler.Schedule(Debounce, Check);
    }

    private void Check()
    {
        _pending = null;
        if (_disposed || _window.Read() is not ExtendedClientState.Lost) return;
        _inLoss = true;
        _lossStart = _scheduler.Now;
        _attempts = 0;
        _step = 0;
        Reapply();
    }

    private void Recheck()
    {
        _pending = null;
        if (_disposed) return;
        switch (_window.Read())
        {
            case ExtendedClientState.Extended:
                _inLoss = false;
                _log($"TITLE BAR: the extended client area was re-applied after {Describe()}");
                break;
            case ExtendedClientState.Lost:
                Reapply();
                break;
            default:
                // Unknown (mid-move, minimized): no toggle, but the ladder goes on.
                ScheduleRecheck();
                break;
        }
    }

    private void Reapply()
    {
        _attempts++;
        _window.Reapply();
        ScheduleRecheck();
    }

    private void ScheduleRecheck()
    {
        TimeSpan delay = _step < Ladder.Length ? Ladder[_step] : Every;
        _step++;
        _pending = _scheduler.Schedule(delay, Recheck);
    }

    /// <summary>"3 attempts (3.5 s)": the re-applies so far and the time since the check that found the loss.</summary>
    private string Describe() => $"{_attempts} {(_attempts == 1 ? "attempt" : "attempts")} ({FormatDuration(_scheduler.Now - _lossStart)})";

    private static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d < TimeSpan.FromMinutes(1)) return d.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        if (d < TimeSpan.FromHours(1)) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        return string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalHours} h {d.Minutes} min");
    }
}

/// <summary>A one-shot <see cref="DispatcherTimer"/> per item; a failing check is traced, never thrown into the dispatcher. Now is a monotonic stopwatch.</summary>
internal sealed class DispatcherScheduler : ITitleBarScheduler
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public TimeSpan Now => Stopwatch.GetElapsedTime(_start);

    public IDisposable Schedule(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(() =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // A failed check only leaves the title bar as it is; it must not take the UI thread down.
            Trace.TraceError("The title bar check failed: " + e);
        }
    }, delay);
}

/// <summary>
/// The main window seen from Win32: it hooks the window procedure for WM_SIZE, WM_DISPLAYCHANGE, WM_SETTINGCHANGE,
/// WM_DPICHANGED and WM_WTSSESSION_CHANGE (registered here), plus <see cref="Window.Activated"/>, and reads the frame
/// through <see cref="WindowFrame"/>.
/// </summary>
internal sealed class Win32TitleBarWindow : ITitleBarWindow
{
    private const uint WmSize = 0x0005;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmDpiChanged = 0x02E0;

    private readonly Window _window;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private readonly nint _hwnd;
    private readonly bool _sessionRegistered;

    public Win32TitleBarWindow(Window window)
    {
        _window = window;
        _hook = OnMessage;
        Win32Properties.AddWndProcHookCallback(window, _hook);
        window.Activated += OnActivated;
        _hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        _sessionRegistered = WindowFrame.RegisterSessionNotification(_hwnd);
    }

    public event EventHandler? Changed;

    public ExtendedClientState Read()
    {
        if (!WindowFrame.TryRead(_hwnd, out FrameRect window, out FrameRect client, out FrameRect work)) return ExtendedClientState.Unknown;
        WindowState state = _window.WindowState;

        // Maximized, the rects look alike either way: ask the window about the middle of its off-screen top strip.
        int hit = state is WindowState.Maximized ? WindowFrame.HitTest(_hwnd, window.Left + (window.Right - window.Left) / 2, window.Top + 1) : 0;
        return ExtendedClientArea.Classify(ToScreen(window), ToScreen(client), state, ToScreen(work), hit);
    }

    public void Reapply()
    {
        _window.ExtendClientAreaToDecorationsHint = false;
        _window.ExtendClientAreaToDecorationsHint = true;
    }

    public void Dispose()
    {
        Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        _window.Activated -= OnActivated;
        if (_sessionRegistered) WindowFrame.UnregisterSessionNotification(_hwnd);
    }

    private static ScreenRect ToScreen(FrameRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    // Observes only: never handled, so Avalonia's own handling of every message goes on.
    private nint OnMessage(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is WmSize or WmSettingChange or WmDisplayChange or WmWtsSessionChange or WmDpiChanged) Changed?.Invoke(this, EventArgs.Empty);
        return 0;
    }

    private void OnActivated(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
