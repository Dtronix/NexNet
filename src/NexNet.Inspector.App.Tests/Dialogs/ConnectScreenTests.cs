using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Connect;
using NexNet.Inspector.App.Views.Home;

namespace NexNet.Inspector.App.Tests.Dialogs;

/// <summary>The main window's empty state and Connect dialog together, as the connect screen shows them.</summary>
[NotInParallel("avalonia")]
public sealed class ConnectScreenTests
{
    [Test]
    public async Task Ctrl_K_opens_the_Connect_dialog_with_the_endpoint_focused_and_Esc_closes_it()
    {
        (bool opened, bool endpointFocused, double width, bool closed, bool homeShown) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel viewModel = new(themes, null);
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                UiPump.Frames();
                window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
                UiPump.Frames();
                DialogHost host = window.FindControl<DialogHost>("Dialogs")!;
                ConnectDialog? dialog = host.GetVisualDescendants().OfType<ConnectDialog>().FirstOrDefault();
                bool focused = dialog is not null && ReferenceEquals(window.FocusManager?.GetFocusedElement(), dialog.FindControl<TextBox>("EndpointBox"));
                double w = dialog?.FindControl<DialogFrame>("Frame")?.Bounds.Width ?? 0;
                bool opened = host.IsOpen && viewModel.Dialog is ConnectDialogViewModel;
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiPump.Frames();
                return (opened, focused, w, !host.IsOpen && viewModel.Dialog is null, window.FindControl<HomeView>("Home")!.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(opened).IsTrue();
        await Assert.That(endpointFocused).IsTrue();
        await Assert.That(width).IsEqualTo(540);
        await Assert.That(closed).IsTrue();
        await Assert.That(homeShown).IsTrue();
    }

    [Test]
    public async Task The_title_bar_Connect_pill_opens_the_dialog_and_plus_opens_a_recording()
    {
        (bool connectOpened, int picks) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            MainWindowViewModel viewModel = new(themes, null);
            int picked = 0;
            viewModel.Home.PickFile = () =>
            {
                picked++;
                return Task.FromResult<string?>(null);
            };
            MainWindow window = new() { DataContext = viewModel };
            window.Show();
            try
            {
                UiPump.Frames();
                Views.Shell.TitleBar bar = window.FindControl<Views.Shell.TitleBar>("TitleBar")!;
                Click(window, bar.FindControl<Button>("AddButton")!);
                Click(window, bar.FindControl<Button>("ConnectButton")!);
                UiPump.Frames();
                bool opened = viewModel.Dialog is ConnectDialogViewModel && window.FindControl<DialogHost>("Dialogs")!.IsOpen;
                return (opened, picked);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(picks).IsEqualTo(1);
        await Assert.That(connectOpened).IsTrue();
    }

    private static void Click(Window window, Control target)
    {
        Avalonia.Point centre = target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
    }

    [Test]
    public async Task The_connect_scenario_shows_the_reached_probe_and_the_recents()
    {
        (bool open, string? status, int endpoints, int recordings) = await HeadlessSession.Run(() =>
        {
            using ThemeService themes = new(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light);
            Window window = ConnectScreen.Create(new CaptureContext(AppTheme.Light, themes));
            window.Show();
            try
            {
                UiPump.Frames();
                if (window.DataContext is not MainWindowViewModel { Dialog: ConnectDialogViewModel dialog } vm)
                    throw new InvalidOperationException("the connect screen has no Connect dialog");
                return (window.FindControl<DialogHost>("Dialogs")!.IsOpen, dialog.StatusText, vm.Home.RecentEndpoints.Count, vm.Home.RecentRecordings.Count);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(open).IsTrue();
        await Assert.That(status).IsEqualTo(ConnectScreen.Reached);
        await Assert.That(endpoints).IsEqualTo(3);
        await Assert.That(recordings).IsEqualTo(3);
    }
}
