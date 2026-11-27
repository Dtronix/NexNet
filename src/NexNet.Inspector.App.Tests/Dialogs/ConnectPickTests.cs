using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Controls;
using NexNet.Inspector.App.Model;
using NexNet.Inspector.App.Services;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views;
using NexNet.Inspector.App.Views.Connect;
using NexNet.Inspector.App.Views.Shell;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Dialogs;

/// <summary>
/// I37: Connect against a live tapped Chat server through the real input path: headless pointer clicks on the dialog's
/// Connect button and on the session popover's rows while the dialog is open in its DialogHost (whose focus trap once
/// sent the row's focus back into the dialog, releasing the press and swallowing the click, so Connect waited forever),
/// ending in an attached live document; one session attaching without asking; Esc and Cancel while the pick waits; a
/// session that ended before the pick.
/// </summary>
[NotInParallel("avalonia")]
public sealed class ConnectPickTests
{
    /// <summary>Set to an inspector endpoint (and its token) to run the click flow against a real server (the sample's serve).</summary>
    public const string LiveEndpointVariable = "NEXNET_INSPECTOR_LIVE_ENDPOINT";

    public const string LiveTokenVariable = "NEXNET_INSPECTOR_LIVE_TOKEN";

    [Test]
    public async Task Clicking_a_session_row_while_the_Connect_dialog_is_open_attaches_it_and_frames_arrive()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            using Fixture f = Fixture.Start(clients: 2);
            f.ClickConnect();
            f.PumpUntil(() => f.Area.IsPopoverOpen && f.Area.Sessions.Count == 2, "the session popover");
            (string? prompt, StatusTone tone, string label, bool busy) = (f.Dialog.StatusText, f.Dialog.StatusTone, f.Dialog.PrimaryLabel, f.Dialog.IsBusy);
            bool rowFocused = f.Window.FocusManager?.GetFocusedElement() is Button { DataContext: SessionChoice };

            SessionChoice picked = f.Area.Sessions[0];
            f.Click(f.Row(picked.Info.Id));
            f.PumpUntil(() => f.Model.Documents.Active is LiveDocumentViewModel { State: LiveState.Live }, "the live document");
            LiveDocumentViewModel live = (LiveDocumentViewModel)f.Model.Documents.Active!;
            foreach (var client in f.Clients) Fixture.Run(client.Proxy.Join("ada").AsTask());
            f.PumpUntil(() => live.FrameCount > 0, "frames from the attached session");
            UiPump.Frames();
            TitleBar bar = f.Window.FindControl<TitleBar>("TitleBar")!;
            return (prompt, tone, label, busy, rowFocused, Closed: f.Model.Dialog is null, f.Host.IsOpen, Attached: live.AttachedSession!.Value.Id, Picked: picked.Info.Id,
                SessionText: bar.FindControl<TextBlock>("SessionIdText")!.Text, picked.IdText, f.Area.IsChoosing, f.Area.IsPopoverOpen);
        });

        await Assert.That(shown.prompt).IsEqualTo(ConnectDialogViewModel.PickPrompt);
        await Assert.That(shown.tone).IsEqualTo(StatusTone.Neutral);
        await Assert.That(shown.label).IsEqualTo("Connect");
        await Assert.That(shown.busy).IsTrue();
        await Assert.That(shown.rowFocused).IsTrue();
        await Assert.That(shown.Closed).IsTrue();
        await Assert.That(shown.IsOpen).IsFalse();
        await Assert.That(shown.Attached).IsEqualTo(shown.Picked);
        await Assert.That(shown.SessionText).IsEqualTo(shown.IdText);
        await Assert.That(shown.IsChoosing).IsFalse();
        await Assert.That(shown.IsPopoverOpen).IsFalse();
    }

    [Test]
    public async Task One_session_attaches_without_asking()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            using Fixture f = Fixture.Start(clients: 1);
            int opens = 0;
            f.Area.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SessionAreaViewModel.IsPopoverOpen) && f.Area.IsPopoverOpen) opens++;
            };
            f.ClickConnect();
            f.PumpUntil(() => f.Model.Documents.Active is LiveDocumentViewModel { State: LiveState.Live }, "the live document");
            UiPump.Frames();
            return (opens, Closed: f.Model.Dialog is null, f.Area.HasLive, f.Area.SessionText);
        });

        await Assert.That(shown.opens).IsEqualTo(0);
        await Assert.That(shown.Closed).IsTrue();
        await Assert.That(shown.HasLive).IsTrue();
        await Assert.That(shown.SessionText).IsNotEqualTo("—");
    }

    [Test]
    public async Task Esc_leaves_the_pick_then_closes_the_dialog_and_Cancel_abandons_a_waiting_pick()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            using Fixture f = Fixture.Start(clients: 2);
            f.ClickConnect();
            f.PumpUntil(() => f.Area.IsPopoverOpen && f.Area.Sessions.Count == 2, "the session popover");

            // Esc in the list closes it: Connect is ready again and the keyboard is back in the dialog.
            f.Key(Key.Escape, PhysicalKey.Escape);
            f.PumpUntil(() => !f.Dialog.IsBusy, "the pick to end");
            (bool popover, bool choosing, bool dialogOpen, string label, bool focusInDialog) = (f.Area.IsPopoverOpen, f.Area.IsChoosing,
                f.Host.IsOpen, f.Dialog.PrimaryLabel, f.Window.FocusManager?.GetFocusedElement() is Visual v && f.Host.IsVisualAncestorOf(v));

            // Connect again, then Cancel while the list waits: the dialog, the popover and the pending connection group go.
            f.ClickConnect();
            f.PumpUntil(() => f.Area.IsPopoverOpen && f.Dialog.IsPicking, "the session popover again");
            f.Dialog.CancelCommand.Execute(null);
            f.PumpUntil(() => !f.Dialog.IsBusy, "the canceled pick to end");
            UiPump.Frames();
            return (popover, choosing, dialogOpen, label, focusInDialog, Closed: f.Model.Dialog is null && f.Dialog.IsClosed,
                AfterPopover: f.Area.IsPopoverOpen, f.Area.HasConnection, Documents: f.Model.Documents.Documents.Count);
        });

        await Assert.That(shown.popover).IsFalse();
        await Assert.That(shown.choosing).IsFalse();
        await Assert.That(shown.dialogOpen).IsTrue();
        await Assert.That(shown.label).IsEqualTo("Connect");
        await Assert.That(shown.focusInDialog).IsTrue();
        await Assert.That(shown.Closed).IsTrue();
        await Assert.That(shown.AfterPopover).IsFalse();
        await Assert.That(shown.HasConnection).IsFalse();
        await Assert.That(shown.Documents).IsEqualTo(0);
    }

    [Test]
    public async Task A_session_that_ended_before_the_pick_shows_the_reason_and_Connect_lists_again()
    {
        var shown = await HeadlessSession.Run(() =>
        {
            using Fixture f = Fixture.Start(clients: 2);
            f.ClickConnect();
            f.PumpUntil(() => f.Area.IsPopoverOpen && f.Area.Sessions.Count == 2, "the session popover");

            // The first client leaves while the list is open (the sample client reconnects every iteration).
            long gone = f.SessionIds[0];
            Fixture.Run(f.Clients[0].DisconnectAsync());
            f.PumpUntil(() => f.Harness.Inspector.GetSessions().All(s => s.Id != gone), "the session to end");
            f.Click(f.Row(gone));
            f.PumpUntil(() => !f.Dialog.IsBusy, "the attach to fail");
            UiPump.Frames();
            (StatusTone tone, string? text, bool open, int documents) = (f.Dialog.StatusTone, f.Dialog.StatusText, f.Host.IsOpen, f.Model.Documents.Documents.Count);

            // Connect lists again: the one session left attaches.
            f.ClickConnect();
            f.PumpUntil(() => f.Model.Documents.Active is LiveDocumentViewModel { State: LiveState.Live }, "the live document");
            UiPump.Frames();
            return (tone, text, open, documents, Gone: gone, Attached: ((LiveDocumentViewModel)f.Model.Documents.Active!).AttachedSession!.Value.Id,
                Remaining: f.SessionIds[1], Closed: f.Model.Dialog is null);
        });

        await Assert.That(shown.tone).IsEqualTo(StatusTone.Error);
        await Assert.That(shown.text).IsEqualTo("Session " + LiveDocumentViewModel.SessionHex(shown.Gone) + " has ended · Connect lists the sessions again");
        await Assert.That(shown.open).IsTrue();
        await Assert.That(shown.documents).IsEqualTo(0);
        await Assert.That(shown.Attached).IsEqualTo(shown.Remaining);
        await Assert.That(shown.Closed).IsTrue();
    }

    [Test]
    public async Task A_live_endpoint_from_the_environment_attaches_by_click()
    {
        // Manual check against real processes (the sample's serve and client); nothing to do without the variable.
        if (Environment.GetEnvironmentVariable(LiveEndpointVariable) is not { Length: > 0 } endpoint) return;
        string token = Environment.GetEnvironmentVariable(LiveTokenVariable) ?? "";
        var shown = await HeadlessSession.Run(() =>
        {
            using Fixture f = Fixture.Open(endpoint, token, null);
            List<string> attempts = [];
            // The sample client's sessions come and go (connect, scenario, disconnect, pause): Connect until one is caught.
            for (int attempt = 0; attempt < 40 && f.Model.Documents.Active is not LiveDocumentViewModel; attempt++)
            {
                f.ClickConnect();
                f.PumpUntil(() => f.Model.Documents.Active is LiveDocumentViewModel || f.Area.IsPopoverOpen && f.Area.Sessions.Count > 0
                    || !f.Dialog.IsBusy, "the attach or the session popover");
                if (f.Area.IsPopoverOpen) f.Click(f.Row(f.Area.Sessions[^1].Info.Id));
                f.PumpUntil(() => f.Model.Documents.Active is LiveDocumentViewModel || !f.Dialog.IsBusy, "the attach");
                attempts.Add(f.Dialog.StatusText ?? "");
                if (f.Model.Documents.Active is not LiveDocumentViewModel) Thread.Sleep(150);
            }

            if (f.Model.Documents.Active is not LiveDocumentViewModel live)
                return (Attempts: attempts, Closed: false, Frames: 0, Session: "", State: LiveState.Detached);
            f.PumpUntil(() => live.FrameCount > 0 || live.State is LiveState.Ended, "frames or the session's end");
            UiPump.Frames();
            return (Attempts: attempts, Closed: f.Model.Dialog is null, Frames: live.FrameCount, Session: f.Area.SessionText, live.State);
        });

        Console.WriteLine($"live endpoint: {shown.Attempts.Count} attempt(s) [{string.Join(" | ", shown.Attempts)}]; {shown.Frames} frames; session {shown.Session}; {shown.State}");
        await Assert.That(shown.Closed).IsTrue().Because(string.Join(" | ", shown.Attempts));
        await Assert.That(shown.Session).IsNotEqualTo("—");
    }

    /// <summary>The main window over a live Chat server (or a given endpoint), its Connect dialog open and filled in.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ThemeService _themes;

        private Fixture(ThemeService themes, ChatServerHarness? harness)
        {
            _themes = themes;
            Harness = harness!;
            Model = new MainWindowViewModel(themes, store: null, new DocumentsViewModel(AvaloniaUiDispatcher.Instance, null));
            Window = new MainWindow { DataContext = Model };
            Window.Show();
            UiPump.Frames();
        }

        public ChatServerHarness Harness { get; }

        public List<NexusClient<ChatClientNexus, ChatClientNexus.ServerProxy>> Clients { get; } = [];

        public List<long> SessionIds { get; } = [];

        public MainWindowViewModel Model { get; }

        public MainWindow Window { get; }

        public ConnectDialogViewModel Dialog { get; private set; } = null!;

        public SessionAreaViewModel Area => Model.Chrome.Session;

        public DialogHost Host => Window.FindControl<DialogHost>("Dialogs")!;

        /// <summary>A tapped Chat server with <paramref name="clients"/> sessions; the dialog points at its endpoint.</summary>
        public static Fixture Start(int clients)
        {
            ChatServerHarness harness = Run(ChatServerHarness.StartAsync(allowRecording: true));
            Fixture f = Open(harness.EndpointAddress, ChatServerHarness.Token, harness);
            for (int i = 0; i < clients; i++)
            {
                f.Clients.Add(Run(harness.ConnectClientAsync()));
                f.SessionIds.Add(Run(harness.WaitForSessionAsync(i + 1)).Id);
            }

            return f;
        }

        public static Fixture Open(string endpoint, string token, ChatServerHarness? harness)
        {
            Fixture f = new(new ThemeService(HeadlessSession.App, new FakeOsTheme(), AppTheme.Light), harness);
            f.Dialog = f.Model.ShowConnect(null)!;
            UiPump.Frames();
            f.Dialog.Connect.Endpoint = endpoint;
            f.Dialog.Connect.Token = token;
            // The typing pause's probe fills the status line first, as it has by the time a user reaches Connect.
            f.PumpUntil(() => f.Dialog.Connect.ProbeState is not (ProbeState.Pending or ProbeState.Probing or ProbeState.Idle), "the probe");
            UiPump.Frames();
            return f;
        }

        public void ClickConnect()
        {
            ConnectDialog view = Host.GetVisualDescendants().OfType<ConnectDialog>().Single();
            Click(view.FindControl<Button>("ConnectButton")!);
        }

        /// <summary>The popover's row for session <paramref name="id"/>.</summary>
        public Button Row(long id)
        {
            UiPump.Frames();
            SessionPopover popover = Window.FindControl<SessionPopover>("SessionPopover")!;
            return popover.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is SessionChoice c && c.Info.Id == id);
        }

        public void Click(Control target)
        {
            Point centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(centre, RawInputModifiers.None);
            Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
            Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
            UiPump.Frames();
        }

        public void Key(Key key, PhysicalKey physical)
        {
            Window.KeyPress(key, RawInputModifiers.None, physical, null);
            UiPump.Frames();
        }

        /// <summary>Runs the UI thread's jobs until <paramref name="done"/>; the connection's work continues there.</summary>
        public void PumpUntil(Func<bool> done, string what)
        {
            long deadline = Environment.TickCount64 + 20_000;
            while (!done())
            {
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException($"Timed out waiting for {what} (status: {Dialog?.StatusText}; busy: {Dialog?.IsBusy}; popover: {Area.IsPopoverOpen}).");
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Thread.Sleep(5);
            }

            Dispatcher.UIThread.RunJobs();
        }

        public static void Run(Task task)
        {
            Pump(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        public static T Run<T>(Task<T> task)
        {
            Pump(() => task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }

        private static void Pump(Func<bool> done)
        {
            long deadline = Environment.TickCount64 + 20_000;
            while (!done() && Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            Window.Close();
            Run(Area.DisposeAsync().AsTask());
            Run(Model.Documents.DisposeAsync().AsTask());
            if (Harness is not null) Run(Harness.DisposeAsync().AsTask());
            _themes.Dispose();
        }
    }
}
