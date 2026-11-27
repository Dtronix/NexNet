using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NexNet.Inspector.App.Checks;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Home;

namespace NexNet.Inspector.App.Tests;

/// <summary>The main window opens headless with its shell, the home screen, minimum size and placement.</summary>
[NotInParallel("avalonia")]
public sealed class HeadlessLaunchTests
{
    private readonly record struct Shown(
        string? Title, double MinWidth, double MinHeight, double TitleBarHeight, string? Hint, bool HasIcon, int Colours, PixelSize Frame);

    [Test]
    public async Task The_main_window_opens_with_the_shell_placeholder()
    {
        Shown shown = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindow window = new() { DataContext = new MainWindowViewModel(themes, null) };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using WriteableBitmap frame = window.CaptureRenderedFrame()!;
                return new Shown(window.Title, window.MinWidth, window.MinHeight,
                    window.FindControl<Views.Shell.TitleBar>("TitleBar")!.Bounds.Height,
                    window.FindControl<HomeView>("Home")!.FindControl<TextBlock>("AppTitle")!.Text, window.Icon is not null,
                    FramePixels.DistinctColours(frame, minimum: 8), frame.PixelSize);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(shown.Title).IsEqualTo("NexNet Inspector");
        await Assert.That(shown.MinWidth).IsEqualTo(1100);
        await Assert.That(shown.MinHeight).IsEqualTo(700);
        await Assert.That(shown.TitleBarHeight).IsEqualTo(36);
        await Assert.That(shown.Hint).IsEqualTo("NexNet Inspector");
        await Assert.That(shown.HasIcon).IsTrue();
        await Assert.That(shown.Colours).IsGreaterThanOrEqualTo(8);
        await Assert.That(shown.Frame).IsEqualTo(new PixelSize(1440, 900));
    }

    [Test]
    public async Task A_saved_placement_is_restored_and_clamped_to_the_minimum()
    {
        (double w, double h, WindowState state, double smallW, double smallH) = await HeadlessSession.Run(() =>
        {
            MainWindow big = new();
            big.Restore(new WindowPlacement(10, 10, 1300, 820, SavedWindowState.Maximized));
            MainWindow small = new();
            small.Restore(new WindowPlacement(10, 10, 640, 480, SavedWindowState.Normal));
            return (big.Width, big.Height, big.WindowState, small.Width, small.Height);
        });

        await Assert.That(w).IsEqualTo(1300);
        await Assert.That(h).IsEqualTo(820);
        await Assert.That(state).IsEqualTo(WindowState.Maximized);
        await Assert.That(smallW).IsEqualTo(1100);
        await Assert.That(smallH).IsEqualTo(700);
    }

    [Test]
    public async Task The_placement_reports_the_normal_size_and_state()
    {
        WindowPlacement placement = await HeadlessSession.Run(() =>
        {
            MainWindow window = new() { Width = 1234, Height = 777 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                return window.Placement;
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(placement.Width).IsEqualTo(1234);
        await Assert.That(placement.Height).IsEqualTo(777);
        await Assert.That(placement.State).IsEqualTo(SavedWindowState.Normal);
    }
}
