using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NexNet.Inspector.App.Settings;
using NexNet.Inspector.App.Tests.Capture;
using NexNet.Inspector.App.Tests.Documents;
using NexNet.Inspector.App.ViewModels;
using NexNet.Inspector.App.Views.Home;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.App.Tests.Home;

/// <summary>The empty window: recents from the settings, the drop target, the action cards and their states.</summary>
public sealed class HomeTests
{
    private sealed class Calls
    {
        public readonly List<RecentEndpoint?> Connects = [];
        public readonly List<string> Opens = [];
        public Exception? OpenFailure;

        public HomeViewModel Home(AppSettings settings) => new(() => settings, new ManualTime(ConnectScreen.Now), Connects.Add, path =>
        {
            Opens.Add(path);
            return OpenFailure is { } e ? Task.FromException(e) : Task.CompletedTask;
        });
    }

    [Test]
    public async Task Relative_dates_read_today_yesterday_then_the_date()
    {
        DateTimeOffset now = new(2026, 11, 20, 0, 30, 0, TimeSpan.Zero);
        await Assert.That(HomeViewModel.RelativeDate(now.AddMinutes(-20), now)).IsEqualTo("today");
        await Assert.That(HomeViewModel.RelativeDate(now.AddMinutes(-40), now)).IsEqualTo("yesterday");
        await Assert.That(HomeViewModel.RelativeDate(now.AddDays(-2), now)).IsEqualTo("Nov 18");
        await Assert.That(HomeViewModel.RelativeDate(now.AddYears(-1), now)).IsEqualTo("Nov 20, 2025");
        await Assert.That(HomeViewModel.SessionCount(1)).IsEqualTo("1 session");
        await Assert.That(HomeViewModel.SessionCount(41)).IsEqualTo("41 sessions");
    }

    [Test]
    public async Task Recents_become_rows_with_kind_name_count_and_date()
    {
        Calls calls = new();
        HomeViewModel home = calls.Home(ConnectScreen.Settings());

        await Assert.That(home.RecentEndpoints.Select(r => (r.Kind, r.Endpoint, r.When))).IsEquivalentTo(
        [
            ("Server", "tcp://10.0.4.12:7711", "today"), ("Client", "uds:///tmp/chat-inspect.sock", "yesterday"),
            ("Server", "quic://staging-chat:7711", "Nov 18"),
        ]);
        await Assert.That(home.RecentRecordings.Select(r => (r.Name, r.Sessions, r.When))).IsEquivalentTo(
        [
            ("server-all.nntl", "3 sessions", "today"), ("chat-repro.nntl", "1 session", "today"),
            ("reconnect-storm.nntl", "41 sessions", "Nov 17"),
        ]);

        home.OpenRecentEndpointCommand.Execute(home.RecentEndpoints[1]);
        await home.OpenRecentRecordingCommand.ExecuteAsync(home.RecentRecordings[2]);
        await Assert.That(calls.Connects.Single()!.Value.Endpoint).IsEqualTo("uds:///tmp/chat-inspect.sock");
        await Assert.That(calls.Opens.Single()).IsEqualTo("/captures/reconnect-storm.nntl");
    }

    [Test]
    public async Task Only_a_recording_lights_the_drop_card_and_a_drop_opens_it()
    {
        Calls calls = new();
        HomeViewModel home = calls.Home(new AppSettings());

        await Assert.That(home.DragEnter(["/tmp/notes.txt"])).IsFalse();
        await Assert.That(home.IsDragOver).IsFalse();
        await Assert.That(home.DragEnter(["/tmp/notes.txt", "/tmp/chat-repro.NNTL"])).IsTrue();
        await Assert.That(home.IsDragOver).IsTrue();
        await Assert.That(home.DropText).IsEqualTo("Drop to open chat-repro.NNTL");
        home.DragLeave();
        await Assert.That(home.IsDragOver).IsFalse();

        await Assert.That(await home.DropAsync(["/tmp/notes.txt"])).IsFalse();
        await Assert.That(await home.DropAsync(["/tmp/a.nntl", "/tmp/b.nntl"])).IsTrue();
        await Assert.That(calls.Opens).IsEquivalentTo(["/tmp/a.nntl"]);
        await Assert.That(home.IsDragOver).IsFalse();
    }

    [Test]
    public async Task A_file_that_will_not_open_is_reported_on_the_home()
    {
        Calls calls = new() { OpenFailure = new Core.Format.NntlFormatException("not a recording") };
        HomeViewModel home = calls.Home(new AppSettings());

        await home.DropAsync(["/tmp/broken.nntl"]);

        await Assert.That(home.HasOpenError).IsTrue();
        await Assert.That(home.OpenError).IsEqualTo("Could not open broken.nntl: not a recording");
    }

    [Test]
    public async Task Opening_a_real_recording_adds_a_document_and_records_it_in_the_recents()
    {
        using TempDirectory temp = new();
        SettingsStore store = new(temp.File("settings.json"));
        await using DocumentsViewModel documents = new(new TestUi(), store, new ManualTime(ConnectScreen.Now));
        HomeViewModel home = new(() => store.Current, new ManualTime(ConnectScreen.Now), static _ => { }, path => documents.OpenRecordingAsync(path));

        await Assert.That(await home.DropAsync([Wait.Fixture("chat-server.nntl")])).IsTrue();
        home.Refresh();

        await Assert.That(documents.HasDocuments).IsTrue();
        await Assert.That(home.HasOpenError).IsFalse();
        await Assert.That(home.RecentRecordings.Single().Name).IsEqualTo("chat-server.nntl");
        await Assert.That(home.RecentRecordings.Single().When).IsEqualTo("today");
    }

    [Test]
    [NotInParallel("avalonia")]
    public async Task The_view_shows_the_recents_or_their_empty_text()
    {
        Calls calls = new();
        (int endpointRows, bool endpointsEmpty, bool recordingsEmpty, string? emptyText) = await HeadlessSession.Run(() =>
        {
            AppSettings settings = ConnectScreen.Settings() with { RecentFiles = [] };
            HomeView view = new() { DataContext = calls.Home(settings) };
            Window window = new() { Width = 1440, Height = 864, Content = view };
            window.Show();
            try
            {
                UiPump.Frames();
                ListCard endpoints = view.FindControl<ListCard>("RecentEndpointsCard")!;
                ListCard recordings = view.FindControl<ListCard>("RecentRecordingsCard")!;
                TextBlock empty = recordings.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "PART_Empty");
                return (endpoints.GetRealizedContainers().Count(), endpoints.Classes.Contains(":empty"), recordings.Classes.Contains(":empty"),
                    empty.IsEffectivelyVisible ? empty.Text : null);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(endpointRows).IsEqualTo(3);
        await Assert.That(endpointsEmpty).IsFalse();
        await Assert.That(recordingsEmpty).IsTrue();
        await Assert.That(emptyText).IsEqualTo("No recent recordings yet");
    }

    [Test]
    [NotInParallel("avalonia")]
    public async Task Clicking_a_recent_row_or_pressing_Enter_on_it_opens_it()
    {
        Calls calls = new();
        await HeadlessSession.Run(() =>
        {
            HomeView view = new() { DataContext = calls.Home(ConnectScreen.Settings()) };
            Window window = new() { Width = 1440, Height = 864, Content = view };
            window.Show();
            try
            {
                UiPump.Frames();
                ListCard recordings = view.FindControl<ListCard>("RecentRecordingsCard")!;
                Control second = recordings.ContainerFromIndex(1)!;
                Point centre = second.TranslatePoint(new Point(second.Bounds.Width / 2, second.Bounds.Height / 2), window)!.Value;
                window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);

                ListCard endpoints = view.FindControl<ListCard>("RecentEndpointsCard")!;
                endpoints.SelectedIndex = 2;
                endpoints.ContainerFromIndex(2)!.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                UiPump.Frames();
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(calls.Opens).IsEquivalentTo(["/captures/chat-repro.nntl"]);
        await Assert.That(calls.Connects.Single()!.Value.Endpoint).IsEqualTo("quic://staging-chat:7711");
    }

    [Test]
    [NotInParallel("avalonia")]
    public async Task Dragging_a_recording_over_the_window_lights_the_drop_card_and_dropping_opens_it()
    {
        Calls calls = new();
        (bool lit, string? text, bool unlit) = await HeadlessSession.Run(() =>
        {
            HomeView view = new() { DataContext = calls.Home(new AppSettings()) };
            Window window = new() { Width = 1440, Height = 864, Content = view };
            window.Show();
            try
            {
                UiPump.Frames();
                ActionCard open = view.FindControl<ActionCard>("OpenCard")!;
                // The real file of the fixture, through the platform's storage (an IStorageFile is not implementable).
                string fixture = Wait.Fixture("chat-server.nntl");
                IStorageFile file = window.StorageProvider.TryGetFileFromPathAsync(new Uri(fixture)).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("no storage provider in the headless session");
                DataTransfer data = new();
                data.Add(DataTransferItem.CreateFile(file));
                Point anywhere = new(20, 20);

                window.DragDrop(anywhere, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                UiPump.Frames();
                bool lit = open.Classes.Contains(":dragover");
                string? shown = open.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "PART_DropText") is { IsEffectivelyVisible: true } t
                    ? t.Text
                    : null;
                window.DragDrop(anywhere, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
                UiPump.Frames();
                return (lit, shown, !open.Classes.Contains(":dragover"));
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(lit).IsTrue();
        await Assert.That(text).IsEqualTo("Drop to open chat-server.nntl");
        await Assert.That(unlit).IsTrue();
        await Assert.That(calls.Opens).IsEquivalentTo([Wait.Fixture("chat-server.nntl")]);
    }

    [Test]
    [NotInParallel("avalonia")]
    public async Task The_action_cards_draw_the_dashed_drop_target_and_their_states()
    {
        (bool dashed, bool solidHidden, bool focusRing, bool connectDashed) = await HeadlessSession.Run(() =>
        {
            Control set = HomeGallery.CardSet(GalleryState.Focus);
            Window window = new() { Width = 900, Height = 400, Content = set };
            window.Show();
            try
            {
                UiPump.Frames();
                ActionCard[] cards = [.. set.GetVisualDescendants().OfType<ActionCard>()];
                ActionCard connect = cards[0];
                ActionCard open = cards[1];
                Avalonia.Controls.Shapes.Rectangle dash = open.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().First(r => r.Name == "PART_Dash");
                Border card = open.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Card");
                Border ring = connect.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_FocusRing");
                Avalonia.Controls.Shapes.Rectangle connectDash = connect.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().First(r => r.Name == "PART_Dash");
                return (dash.IsVisible && dash.StrokeDashArray is { Count: > 0 }, card.BorderThickness == default, ring.IsVisible, connectDash.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        await Assert.That(dashed).IsTrue();
        await Assert.That(solidHidden).IsTrue();
        await Assert.That(focusRing).IsTrue();
        await Assert.That(connectDashed).IsFalse();
    }
}
